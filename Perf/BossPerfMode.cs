using System;
using System.Reflection;
using System.Text;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace EngineOptimizer.Perf;

// v0.8.3 — режим максимального FPS в боях с боссами.
// v0.8.5 — то же самое для роста тайлов мира: на время боя
// приостанавливаются UpdateWorld_OvergroundTile/UndergroundTile и
// HandleTileGrowth (трава/лианы/кактусы). По CPU-профилю это ~0.8 мс
// кадра; замедление роста на минуту боя визуально незаметно.
// В мультиплеере мир обновляет сервер — у клиента эти хуки просто
// никогда не срабатывают.
//
// По свежим CPU-профилям главного потока главный пожиратель кадра в бою —
// дым из мода CalamityFables: CalamityFables.Particles.
// MergeBlendSmokeRenderTarget.DrawSmoke съедал ~21% кадра (6.4 мс).
// Вторичные цели: heat distortion и storm effects (ванильные тяжёлые
// шейдерные проходы, в бою почти не заметные).
//
// Механизм единый: на каждую цель ставится IL-хук с ранним выходом —
// пока идёт бой, тело метода не выполняется. Всё консервативно: хук
// ставится только на найденный метод с поддерживаемым типом возврата;
// любая ошибка отключает часть фичи, но не мод. Опция в конфиге.
public sealed class BossPerfMode : ModSystem
{
    public const string SmokeTypeName = "CalamityFables.Particles.MergeBlendSmokeRenderTarget";
    public const string SmokeMethodName = "DrawSmoke";

    // Задержка возврата эффектов после конца боя: чтобы между фазами
    // босса (голова/руки Moon Lord и т.п.) картинка не мигала.
    public const double RestoreLingerMs = 2000.0;

    // Назначается в ProfilerSystem.Load — как у остальных подсистем Perf.
    public static Action<string> Log;

    public static bool Active { get; private set; }

    // true — IL-хук на дым установлен (мод CalamityFables найден).
    public static bool SmokeHooked => _smokeInstalled;

    // Сколько хуков раннего выхода стоит на рост тайлов мира.
    public static int TileHooksInstalled => _tileHooks;

    public static bool AnyHook => _smokeInstalled || _tileHooks > 0;

    private static readonly (string Type, string Method)[] TileTargets =
    {
        ("Terraria.WorldGen", "UpdateWorld_OvergroundTile"),
        ("Terraria.WorldGen", "UpdateWorld_UndergroundTile"),
        ("Terraria.WorldGen", "HandleTileGrowth"),
    };

    private static bool _smokeInstalled;
    private static int _tileHooks;
    private static bool _tileAttempted;
    private static bool _unavailable;
    private static long _lastInstallTry;
    private static long _lastBossSeen;
    private static bool _savedHeat;
    private static bool _savedStorm;
    private static bool _changed;

    public override void Load()
    {
        Reset();
    }

    public override void Unload()
    {
        RestoreEffects();
        Reset();
    }

    public override void OnWorldLoad()
    {
        // Не тянем состояние боя из прошлого мира (linger-таймер).
        _lastBossSeen = 0;

        // К этому моменту сборки всех включённых модов уже загружены.
        TryInstall();
    }

    public override void OnWorldUnload()
    {
        RestoreEffects();
    }

    public override void PostUpdateEverything()
    {
        if (Main.dedServ)
            return;

        // Повторные попытки поставить хук — на случай поздней загрузки
        // сборки-цели. Редко и дёшево: один рефлексивный поиск в 10 с.
        bool needInstall = (!_smokeInstalled && !_unavailable) || !_tileAttempted;
        if (needInstall && PerfData.MsSince(_lastInstallTry) >= 10000.0)
            TryInstall();

        var config = OptimizerConfig.Instance;
        bool enabled = config != null && config.BossPerfMode;

        bool boss = enabled && ScanBoss();
        if (boss)
            _lastBossSeen = PerfData.Now;

        // Активны, пока босс рядом ИЛИ прошло меньше RestoreLingerMs
        // с момента последнего появления (анти-мигание между фазами).
        bool want = boss || (enabled && _lastBossSeen != 0 && PerfData.MsSince(_lastBossSeen) < RestoreLingerMs);

        if (Main.gameMenu)
            want = false;

        if (want && !Active)
        {
            _savedHeat = Main.UseHeatDistortion;
            _savedStorm = Main.UseStormEffects;
            _changed = true;
            Active = true;
        }
        else if (!want && Active)
        {
            RestoreEffects();
        }

        if (Active)
        {
            // Держим каждый кадр: другие системы могут включить эффекты
            // обратно посреди боя (например, при смене биома).
            Main.UseHeatDistortion = false;
            Main.UseStormEffects = false;
        }
    }

    // Короткая сводка для оверлея: smoke+fx+world.
    public static string HookSummary()
    {
        var sb = new StringBuilder(16);
        if (_smokeInstalled)
            sb.Append("smoke+");
        sb.Append("fx");
        if (_tileHooks > 0)
            sb.Append("+world");
        return sb.ToString();
    }

    // Босс в радиусе 8000 px — общий проход с отложенным автосейвом
    // (ProfilerSystem.BossActive), чтобы не сканировать npc-таблицу дважды.
    private static bool ScanBoss() => ProfilerSystem.BossActive();

    private static void RestoreEffects()
    {
        Active = false;
        if (!_changed)
            return;

        Main.UseHeatDistortion = _savedHeat;
        Main.UseStormEffects = _savedStorm;
        _changed = false;
    }

    // Вызывается из пропатченных методов (главный поток, каждый кадр).
    // false — выполнить оригинальное тело.
    private static bool ShouldSkip() => Active;

    private static void TryInstall()
    {
        bool wantSmoke = !_smokeInstalled && !_unavailable;
        bool wantTiles = !_tileAttempted;
        if (!wantSmoke && !wantTiles)
            return;

        _lastInstallTry = PerfData.Now;

        // v0.8.6: ВАЖНО — секции smoke и tiles независимы. Раньше здесь был
        // ранний return, если CalamityFables не найден (target == null), и
        // хуки роста тайлов НЕ ставились совсем. Теперь smoke-цель просто
        // пропускается (ретрай позже), а tiles ставятся всегда.
        if (wantSmoke)
        {
            try
            {
                var target = FindMethod(SmokeTypeName, SmokeMethodName);
                if (target == null)
                {
                    // мод-цель не загружен — попробуем ещё раз позже
                }
                else if (!IsSupportedReturnType(target.ReturnType))
                {
                    _unavailable = true;
                    Log?.Invoke($"Boss perf: {SmokeTypeName}.{SmokeMethodName} has an unsupported return type, smoke skip disabled.");
                }
                else
                {
                    MonoModHooks.Modify(target, il => EmitEarlyExit(il, target.ReturnType));
                    _smokeInstalled = true;
                    // tML сам снимет хук при выгрузке мода (MonoModHooks привязан
                    // к нашему ModSystem) — Dispose здесь не нужен.

                    Log?.Invoke($"Boss perf: early-exit hook installed on {SmokeTypeName}.{SmokeMethodName}.");
                }
            }
            catch (Exception e)
            {
                _unavailable = true;
                Log?.Invoke("Boss perf: could not hook the smoke renderer: " + e.GetType().Name + ": " + e.Message);
            }
        }

        if (wantTiles)
        {
            _tileAttempted = true;

            foreach (var (typeName, methodName) in TileTargets)
            {
                try
                {
                    var target = FindMethod(typeName, methodName);
                    if (target == null)
                    {
                        Log?.Invoke($"Boss perf: {typeName}.{methodName} not found on this tML build, skipped.");
                        continue;
                    }

                    if (!IsSupportedReturnType(target.ReturnType))
                    {
                        Log?.Invoke($"Boss perf: {typeName}.{methodName} has an unsupported return type, skipped.");
                        continue;
                    }

                    MonoModHooks.Modify(target, il => EmitEarlyExit(il, target.ReturnType));
                    _tileHooks++;
                }
                catch (Exception e)
                {
                    Log?.Invoke($"Boss perf: could not hook {typeName}.{methodName}: " + e.GetType().Name + ": " + e.Message);
                }
            }

            if (_tileHooks > 0)
                Log?.Invoke($"Boss perf: world tile growth is paused during boss fights ({_tileHooks} hook(s)).");
        }
    }

    private static bool IsSupportedReturnType(Type t) =>
        t == typeof(void)
        || t == typeof(bool)
        || t == typeof(byte) || t == typeof(sbyte)
        || t == typeof(short) || t == typeof(ushort)
        || t == typeof(int) || t == typeof(uint)
        || t == typeof(long) || t == typeof(ulong)
        || t == typeof(float) || t == typeof(double)
        || !t.IsValueType;

    // В начало метода добавляется: if (ShouldSkip()) return <default>;
    private static void EmitEarlyExit(ILContext il, Type returnType)
    {
        var c = new ILCursor(il);
        var run = c.DefineLabel();
        c.EmitDelegate<Func<bool>>(ShouldSkip);
        c.Emit(OpCodes.Brfalse_S, run);
        if (returnType == typeof(void))
        {
            // значения не нужно
        }
        else if (returnType == typeof(float))
        {
            c.Emit(OpCodes.Ldc_R4, 0f);
        }
        else if (returnType == typeof(double))
        {
            c.Emit(OpCodes.Ldc_R8, 0.0);
        }
        else if (returnType == typeof(long) || returnType == typeof(ulong))
        {
            c.Emit(OpCodes.Ldc_I4_0);
            c.Emit(OpCodes.Conv_I8);
        }
        else if (returnType == typeof(int) || returnType == typeof(uint)
            || returnType == typeof(short) || returnType == typeof(ushort)
            || returnType == typeof(byte) || returnType == typeof(sbyte)
            || returnType == typeof(bool))
        {
            c.Emit(OpCodes.Ldc_I4_0);
        }
        else
        {
            c.Emit(OpCodes.Ldnull);
        }
        c.Emit(OpCodes.Ret);
        c.MarkLabel(run);
    }

    private static MethodInfo FindMethod(string typeFullName, string methodName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = null;
            try
            {
                type = asm.GetType(typeFullName, false);
            }
            catch
            {
            }

            if (type == null)
                continue;

            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.Name != methodName || m.IsGenericMethodDefinition)
                    continue;

                return m;
            }

            // Тип найден, но метода нет — дальше не ищем.
            return null;
        }

        return null;
    }

    private static void Reset()
    {
        _smokeInstalled = false;
        _tileHooks = 0;
        _tileAttempted = false;
        _unavailable = false;
        _lastInstallTry = 0;
        _lastBossSeen = 0;
        _savedHeat = false;
        _savedStorm = false;
        _changed = false;
        Active = false;
    }
}
