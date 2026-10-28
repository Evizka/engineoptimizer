using System;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace EngineOptimizer.Perf;

// v0.8.3 — режим максимального FPS в боях с боссами.
//
// По свежим CPU-профилям главного потока главный пожиратель кадра в бою —
// дым из мода CalamityFables: CalamityFables.Particles.
// MergeBlendSmokeRenderTarget.DrawSmoke съедал ~21% кадра (6.4 мс).
// Вторичные цели: heat distortion и storm effects (ванильные тяжёлые
// шейдерные проходы, в бою почти не заметные).
//
// Что делает:
//  - на входе в мир ищет сборку CalamityFables и ставит IL-хук на
//    DrawSmoke: пока идёт бой, метод выходит на первом же ret — тело
//    (полный проход отрисовки дыма) не выполняется вовсе;
//  - в бое удерживает Main.UseHeatDistortion = false и
//    Main.UseStormEffects = false, после боя возвращает сохранённые
//    значения;
//  - телеметрия (live.json / timeline.csv) получает флаг boss_perf.
//
// Всё консервативно: хук ставится только если метод найден и возвращает
// void; любая ошибка отключает часть фичи, но не мод. Опция в конфиге.
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
    public static bool SmokeHooked => _installed;

    private static bool _installed;
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
        if (_hook == null && !_unavailable && PerfData.MsSince(_lastInstallTry) >= 10000.0)
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

    // Босс в радиусе 8000 px от игрока — та же логика, что у отложенного
    // автосейва в ProfilerSystem.
    private static bool ScanBoss()
    {
        var npcs = Main.npc;
        var player = Main.LocalPlayer;
        if (npcs == null || player == null)
            return false;

        float px = player.Center.X;
        float py = player.Center.Y;

        for (int i = 0; i < npcs.Length; i++)
        {
            var npc = npcs[i];
            if (!npc.active || !npc.boss || npc.friendly)
                continue;

            float dx = npc.Center.X - px;
            float dy = npc.Center.Y - py;
            if (dx * dx + dy * dy <= 64000000f)
                return true;
        }

        return false;
    }

    private static void RestoreEffects()
    {
        Active = false;
        if (!_changed)
            return;

        Main.UseHeatDistortion = _savedHeat;
        Main.UseStormEffects = _savedStorm;
        _changed = false;
    }

    // Вызывается из пропатченного DrawSmoke (главный поток, каждый кадр
    // отрисовки дыма). false — выполнить оригинальное тело.
    private static bool ShouldSkipSmoke() => Active;

    private static void TryInstall()
    {
        if (_hook != null || _unavailable)
            return;

        _lastInstallTry = PerfData.Now;

        try
        {
            var target = FindSmokeMethod();
            if (target == null)
                return; // мод-цель не загружен — попробуем ещё раз позже

            if (target.ReturnType != typeof(void))
            {
                _unavailable = true;
                Log?.Invoke($"Boss perf: {SmokeTypeName}.{SmokeMethodName} is not void, smoke skip disabled.");
                return;
            }

            MonoModHooks.Modify(target, il =>
            {
                // ret на входе: пока идёт бой, тело метода не выполняется.
                var c = new ILCursor(il);
                var run = c.DefineLabel();
                c.EmitDelegate<Func<bool>>(ShouldSkipSmoke);
                c.Emit(OpCodes.Brfalse_S, run);
                c.Emit(OpCodes.Ret);
                c.MarkLabel(run);
            });
            _installed = true;
            // tML сам снимет хук при выгрузке мода (MonoModHooks привязан
            // к нашему ModSystem) — Dispose здесь не нужен.

            Log?.Invoke($"Boss perf: early-exit hook installed on {SmokeTypeName}.{SmokeMethodName}.");
        }
        catch (Exception e)
        {
            _unavailable = true;
            Log?.Invoke("Boss perf: could not hook the smoke renderer: " + e.GetType().Name + ": " + e.Message);
        }
    }

    private static MethodInfo FindSmokeMethod()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = null;
            try
            {
                type = asm.GetType(SmokeTypeName, false);
            }
            catch
            {
            }

            if (type == null)
                continue;

            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.Name != SmokeMethodName || m.ReturnType != typeof(void))
                    continue;
                if (m.IsGenericMethodDefinition)
                    continue;

                return m;
            }

            // Тип найден, но подходящего метода нет — дальше не ищем.
            return null;
        }

        return null;
    }

    private static void Reset()
    {
        _installed = false;
        _unavailable = false;
        _lastInstallTry = 0;
        _lastBossSeen = 0;
        _savedHeat = false;
        _savedStorm = false;
        _changed = false;
        Active = false;
    }
}
