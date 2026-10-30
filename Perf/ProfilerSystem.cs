using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.UI;
using Terraria.ModLoader;

namespace EngineOptimizer.Perf;

// Главная система мода: хуки движка (тайминги кадра и фаз отрисовки),
// GC-режим низких пауз, отложенный автосейв в бою, оверлей F6 и баннер
// предзагрузки. Отключение автосейва и переключение оверлея живут здесь.
public sealed class ProfilerSystem : ModSystem
{
    private const string ToggleKeyName = "Toggle Profiler";
    private const string CursorKeyName = "Toggle Hardware Cursor";
    private const string PreloadKeyName = "Toggle World Preload";
    private const string PresetKeyName = "Cycle Preset";

    public static ModKeybind ToggleKey;
    public static ModKeybind CursorKey;
    public static ModKeybind PreloadKey;
    public static ModKeybind PresetKey;

    private static OptimizerConfig Settings;

    private bool _configApplied;
    private bool _gcSmoothing;
    private bool _threadPoolTuned;
    private GCLatencyMode _previousLatency;

    private const int OverlayRows = 9; // v1.1: +строка preset

    // Оверлей кешируется: строки пересобираются 4 раза в секунду.
    private static readonly string[] OverlayLabelsLeft = new string[OverlayRows];
    private static readonly string[] OverlayValuesLeft = new string[OverlayRows];
    private static readonly Color[] OverlayColorsLeft = new Color[OverlayRows];
    private static readonly string[] OverlayLabelsRight = new string[OverlayRows];
    private static readonly string[] OverlayValuesRight = new string[OverlayRows];
    private static readonly Color[] OverlayColorsRight = new Color[OverlayRows];

    private static string _overlayFps = "";
    private static string _overlayFrame = "";
    private static string _overlayGame = "";
    private static string _overlayKeyHint = "F6";
    private static Color _overlayFpsColor = Color.White;
    private static Color _overlayFrameColor = Color.Gainsboro;
    private static long _overlayBuiltAt;

    private static long _deferStartedAt;

    private static long _tPlayers;
    private static long _tNpcs;
    private static long _tGores;
    private static long _tProjectiles;
    private static long _tItems;
    private static long _tDusts;
    private static long _tTime;
    private static long _tWorld;
    private static long _tInvasions;

    static ProfilerSystem()
    {
    }

    private static void ResetSectionTimers()
    {
        _tPlayers = 0;
        _tNpcs = 0;
        _tGores = 0;
        _tProjectiles = 0;
        _tItems = 0;
        _tDusts = 0;
        _tTime = 0;
        _tWorld = 0;
        _tInvasions = 0;
    }

    public override void Load()
    {
        ToggleKey = KeybindLoader.RegisterKeybind(Mod, ToggleKeyName, "F6");
        CursorKey = KeybindLoader.RegisterKeybind(Mod, CursorKeyName, "F7");
        PreloadKey = KeybindLoader.RegisterKeybind(Mod, PreloadKeyName, "F8");
        PresetKey = KeybindLoader.RegisterKeybind(Mod, PresetKeyName, "F9");

        GoreParallel.Log = s => Mod.Logger.Warn(s);
        RainParallel.Log = s => Mod.Logger.Warn(s);
        GoreParallel.Failed = false;
        RainParallel.Failed = false;
        WorldPreloader.Log = s => Mod.Logger.Info(s);
        KeybindProfileGuard.Log = s => Mod.Logger.Info(s);
        KeybindProfileGuard.Reset();
        BossPerfMode.Log = s => Mod.Logger.Info(s);

        On_Main.DoUpdate += OnDoUpdate;
        On_Main.DoDraw += OnDoDraw;
        On_Main.RenderTiles += OnRenderTiles;
        On_Main.RenderTiles2 += OnRenderTiles2;
        On_Main.RenderWalls += OnRenderWalls;
        On_Main.RenderBackground += OnRenderBackground;
        On_Main.DrawDust += OnDrawDust;
        On_Main.DrawProjectiles += OnDrawProjectiles;
        On_Main.DrawNPCs += OnDrawNpcs;
        On_Main.DrawItems += OnDrawItems;
        On_Lighting.LightTiles += OnLightTiles;
        On_Lighting.AddLight_Vector2_float_float_float += OnAddLight;
        On_Gore.Update += OnGoreUpdate;
        On_Rain.Update += OnRainUpdate;
        On_Main.DrawRain += OnDrawRain;
        On_Dust.NewDust += OnNewDust;
        On_Main.DoUpdate_AutoSave += OnDoUpdateAutoSave;
        On_WorldGen.SectionTileFrameWithCheck += OnSectionTileFrameWithCheck;

        // v0.8.11: кто ест время NPC.AI (по типам) — для live.json и итога.
        NpcAiProfiler.Hook();
    }

    public override void Unload()
    {
        On_Main.DoUpdate -= OnDoUpdate;
        On_Main.DoDraw -= OnDoDraw;
        On_Main.RenderTiles -= OnRenderTiles;
        On_Main.RenderTiles2 -= OnRenderTiles2;
        On_Main.RenderWalls -= OnRenderWalls;
        On_Main.RenderBackground -= OnRenderBackground;
        On_Main.DrawDust -= OnDrawDust;
        On_Main.DrawProjectiles -= OnDrawProjectiles;
        On_Main.DrawNPCs -= OnDrawNpcs;
        On_Main.DrawItems -= OnDrawItems;
        On_Lighting.LightTiles -= OnLightTiles;
        On_Lighting.AddLight_Vector2_float_float_float -= OnAddLight;
        On_Gore.Update -= OnGoreUpdate;
        On_Rain.Update -= OnRainUpdate;
        On_Main.DrawRain -= OnDrawRain;
        On_Dust.NewDust -= OnNewDust;
        On_Main.DoUpdate_AutoSave -= OnDoUpdateAutoSave;
        On_WorldGen.SectionTileFrameWithCheck -= OnSectionTileFrameWithCheck;

        NpcAiProfiler.Unhook();

        GoreParallel.Log = null;
        RainParallel.Log = null;
        WorldPreloader.Reset();
        KeybindProfileGuard.Log = null;
        KeybindProfileGuard.Reset();
        BossPerfMode.Log = null;

        Telemetry.Flush(1500);
        AsyncFileWriter.Stop();

        RestoreProcessPriority();
        RestoreGc();

        Settings = null;
        ToggleKey = null;
        CursorKey = null;
        PreloadKey = null;
        PresetKey = null;
        _overlayBuiltAt = 0;
        _deferStartedAt = 0;
        ResetSectionTimers();
    }

    public override void OnWorldLoad()
    {
        ApplyConfig();
        ApplyGcSettings(true);
        TuneThreadPool();
        ApplyProcessPriority();

        GoreParallel.Reset();
        RainParallel.Reset();
        ResetSectionTimers();
        PerfData.ResetAll();
        PerfLog.Reset();
        Telemetry.Reset();
        Telemetry.WriteSession();

        RuntimeConfig.SetPreset(OptimizerConfig.Instance?.Preset ?? OptimizerPreset.Balanced);

        // v1.2: если найден курсорный мод-конфликт — сказать один раз.
        SmoothCursorSystem.AnnounceConflictOnce();

        if (RuntimeConfig.PreloadWorld && !WorldPreloader.UserDisabled)
            WorldPreloader.Begin();

        Mod.Logger.Info($"World loaded. preset={RuntimeConfig.PresetName}, GC server={System.Runtime.GCSettings.IsServerGC}, latency={System.Runtime.GCSettings.LatencyMode}, cores={Environment.ProcessorCount}, async telemetry=on");
    }

    public override void OnWorldUnload()
    {
        ApplyConfig();
        WorldPreloader.Reset();
        Telemetry.Flush(1500);

        // v0.8.9: итог «от чего лагало» — last-session.txt в папке
        // телеметрии (отдаётся через tools/send-report.ps1 или прямо).
        Telemetry.WriteSummary();

        try
        {
            if (PerfData.SaveReport)
            {
                string report = PerfLog.Save();
                if (report != null)
                    Mod.Logger.Info("Performance report saved to " + report);
            }
        }
        catch (Exception e)
        {
            Mod.Logger.Error("Failed to save the performance report: " + e?.ToString());
        }
        finally
        {
            PerfLog.Reset();
        }

        RestoreProcessPriority();
        RestoreGc();
    }

    public override void PostUpdateEverything()
    {
        ApplyConfig();
        ApplyGcSettings(false);
        TuneThreadPool();

        // v0.8.3: фидбек переключения — раньше в меню/при скрытом оверлее
        // нажатие F6 выглядело как «не работает». Теперь всегда видно,
        // что произошло, а подсказка в панели показывает реальную клавишу.
        if (ToggleKey != null && ToggleKey.JustPressed)
        {
            PerfData.Visible = !PerfData.Visible;
            if (!Main.gameMenu && !Main.dedServ)
                Main.NewText(PerfData.Visible ? "Engine Optimizer: overlay ON" : "Engine Optimizer: overlay OFF", 150, 220, 130);
        }

        // v0.8.10: аварийный выключатель аппаратного курсора (F7). Если
        // курсор пропал (пустая картинка/конфликт модов) — мышь в меню
        // не работает; клавиша мгновенно возвращает ванильный курсор.
        if (CursorKey != null && CursorKey.JustPressed)
        {
            SmoothCursorSystem.UserDisabled = !SmoothCursorSystem.UserDisabled;
            if (SmoothCursorSystem.UserDisabled)
            {
                SmoothCursorSystem.Disable();
                if (!Main.dedServ)
                    Main.NewText("Engine Optimizer: hardware cursor OFF (vanilla) - F7 to re-enable", 255, 140, 60);
            }
            else if (!Main.dedServ)
            {
                Main.NewText("Engine Optimizer: hardware cursor ON", 150, 220, 130);
            }
        }

        // v1.0: вкл/выкл предзагрузки мира на лету (F8). OFF останавливает
        // текущую прогрузку немедленно (миникарта размораживается), ON
        // начинает с текущего положения игрока. Действует до перезапуска
        // игры; состояние на входе в мир задаёт конфиг PreloadWorld.
        if (PreloadKey != null && PreloadKey.JustPressed)
        {
            WorldPreloader.UserDisabled = !WorldPreloader.UserDisabled;
            if (WorldPreloader.UserDisabled)
            {
                WorldPreloader.Reset();
                if (!Main.dedServ)
                    Main.NewText("Engine Optimizer: world preload OFF - F8 to re-enable", 255, 140, 60);
            }
            else
            {
                if (!Main.gameMenu && !Main.dedServ)
                    WorldPreloader.Begin();
                if (!Main.dedServ)
                    Main.NewText("Engine Optimizer: world preload ON", 150, 220, 130);
            }
        }

        // v1.1: глобальный пресет (F9): balanced -> max fps -> diagnostics.
        // Переключает все оптимизации разом; действует до перезапуска игры
        // (умолчание на входе в мир — из конфига мода).
        if (PresetKey != null && PresetKey.JustPressed)
        {
            var preset = RuntimeConfig.Cycle();
            string what = preset == OptimizerPreset.MaxFps
                ? "all optimizations on"
                : preset == OptimizerPreset.DiagnosticsOnly
                    ? "all engine patches off, diagnostics only"
                    : "as configured";
            if (!Main.dedServ)
                Main.NewText($"Engine Optimizer preset: {RuntimeConfig.PresetName} ({what}) - F9 to change", 130, 190, 255);
            Mod.Logger.Info($"Preset changed to {RuntimeConfig.PresetName}");
        }

        // v0.8.6: пока идёт бой, предзагрузка мира на паузе — её бюджет
        // (до PreloadBudgetMs = 3 мс/кадр) нужнее кадру боя. После конца
        // боя (вместе с linger-окном BossPerfMode) продолжается сама,
        // с места остановки.
        if (!BossPerfMode.Active)
            WorldPreloader.Update(Settings?.PreloadBudgetMs ?? 8);
    }

    // ---- тайминги секций тика ----

    public override void PreUpdatePlayers() => _tPlayers = PerfData.Now;

    public override void PostUpdatePlayers()
    {
        if (_tPlayers != 0)
            PerfData.AddUpdPlayers(PerfData.MsSince(_tPlayers));
        _tPlayers = 0;
    }

    public override void PreUpdateNPCs()
    {
        _tNpcs = PerfData.Now;
        NpcAiProfiler.BeginFrame();
    }

    public override void PostUpdateNPCs()
    {
        if (_tNpcs != 0)
            PerfData.AddUpdNpcs(PerfData.MsSince(_tNpcs));
        _tNpcs = 0;
        NpcAiProfiler.EndFrame();
    }

    public override void PreUpdateGores()
    {
        _tGores = PerfData.Now;
        GoreParallel.Run();
    }

    public override void PostUpdateGores()
    {
        if (_tGores != 0)
            PerfData.AddUpdGores(PerfData.MsSince(_tGores));
        _tGores = 0;
        GoreParallel.End();
    }

    public override void PreUpdateProjectiles() => _tProjectiles = PerfData.Now;

    public override void PostUpdateProjectiles()
    {
        if (_tProjectiles != 0)
            PerfData.AddUpdProjectiles(PerfData.MsSince(_tProjectiles));
        _tProjectiles = 0;
    }

    public override void PreUpdateItems() => _tItems = PerfData.Now;

    public override void PostUpdateItems()
    {
        if (_tItems != 0)
            PerfData.AddUpdItems(PerfData.MsSince(_tItems));
        _tItems = 0;
    }

    public override void PreUpdateDusts() => _tDusts = PerfData.Now;

    public override void PostUpdateDusts()
    {
        if (_tDusts != 0)
            PerfData.AddUpdDust(PerfData.MsSince(_tDusts));
        _tDusts = 0;
    }

    public override void PreUpdateTime() => _tTime = PerfData.Now;

    public override void PostUpdateTime()
    {
        if (_tTime != 0)
            PerfData.AddUpdTime(PerfData.MsSince(_tTime));
        _tTime = 0;
    }

    public override void PreUpdateWorld() => _tWorld = PerfData.Now;

    public override void PostUpdateWorld()
    {
        if (_tWorld != 0)
            PerfData.AddUpdWorld(PerfData.MsSince(_tWorld));
        _tWorld = 0;
    }

    public override void PreUpdateInvasions() => _tInvasions = PerfData.Now;

    public override void PostUpdateInvasions()
    {
        if (_tInvasions != 0)
            PerfData.AddUpdInvasions(PerfData.MsSince(_tInvasions));
        _tInvasions = 0;
    }

    // ---- настройки / GC / пул потоков ----

    private void ApplyConfig()
    {
        if (_configApplied)
            return;

        var config = OptimizerConfig.Instance;
        if (config == null)
            return;

        Settings = config;
        PerfData.Visible = config.ShowOverlayOnStart;
        PerfData.SaveReport = config.SaveReportOnWorldExit;
        _configApplied = true;
    }

    private void ApplyGcSettings(bool want)
    {
        if (!RuntimeConfig.ReduceGcPauses || Main.dedServ)
            want = false;
        else if (!want)
            want = !Main.gameMenu;

        if (want == _gcSmoothing)
            return;

        try
        {
            if (want)
            {
                _previousLatency = System.Runtime.GCSettings.LatencyMode;
                System.Runtime.GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                _gcSmoothing = true;
                Mod.Logger.Info("GC latency mode " + _previousLatency.ToString() + " -> " + System.Runtime.GCSettings.LatencyMode.ToString());
            }
            else
            {
                System.Runtime.GCSettings.LatencyMode = _previousLatency;
                _gcSmoothing = false;
                Mod.Logger.Info("GC latency mode restored to " + System.Runtime.GCSettings.LatencyMode.ToString());
            }
        }
        catch (Exception e)
        {
            _gcSmoothing = false;
            Mod.Logger.Warn("Could not apply the GC latency mode: " + e.Message);
        }
    }

    private void RestoreGc()
    {
        if (!_gcSmoothing)
            return;

        try
        {
            System.Runtime.GCSettings.LatencyMode = _previousLatency;
        }
        catch
        {
        }

        _gcSmoothing = false;
    }

    // Минимум воркеров пула = числу ядер: меньше лагов на ramp-up.
    private void TuneThreadPool()
    {
        if (_threadPoolTuned || !RuntimeConfig.ReduceGcPauses)
            return;

        _threadPoolTuned = true;

        try
        {
            int target = Math.Clamp(Environment.ProcessorCount, 4, 32);
            ThreadPool.GetMinThreads(out int workers, out int io);
            if (workers < target)
            {
                if (ThreadPool.SetMinThreads(target, io))
                    Mod.Logger.Info($"ThreadPool minimum worker threads {workers} -> {target}");
            }
        }
        catch
        {
        }
    }

    // v0.8.5: реальный рычаг «выжать CPU» из мода — приоритет процесса.
    // Частота турбобуста управляется ОС/BIOS, мод до неё не дотянется,
    // но AboveNormal убирает вытеснение игры фоном (браузер, Discord,
    // индексация): планировщик отдаёт ядра игре, даже когда те шумят.
    // Поднимаем только с Normal (кастомные приоритеты не трогаем) и
    // только в мире; после выхода — исходный.
    private bool _priorityBoosted;
    private ProcessPriorityClass _previousPriority;

    private void ApplyProcessPriority()
    {
        if (_priorityBoosted || !RuntimeConfig.BoostProcessPriority || Main.dedServ)
            return;

        try
        {
            var current = Process.GetCurrentProcess();
            _previousPriority = current.PriorityClass;
            if (_previousPriority != ProcessPriorityClass.Normal)
                return;

            current.PriorityClass = ProcessPriorityClass.AboveNormal;
            _priorityBoosted = true;
            Mod.Logger.Info("Process priority Normal -> AboveNormal (in world).");
        }
        catch (Exception e)
        {
            Mod.Logger.Warn("Could not boost the process priority: " + e.GetType().Name + ": " + e.Message);
        }
    }

    private void RestoreProcessPriority()
    {
        if (!_priorityBoosted)
            return;

        try
        {
            Process.GetCurrentProcess().PriorityClass = _previousPriority;
            Mod.Logger.Info("Process priority restored to " + _previousPriority + ".");
        }
        catch
        {
        }

        _priorityBoosted = false;
    }

    // ---- хуки движка ----

    private static void OnDoUpdate(On_Main.orig_DoUpdate orig, Main self, ref GameTime gameTime)
    {
        KeybindProfileGuard.Ensure();
        PerfData.OnUpdateStart();

        long start = PerfData.Now;
        try
        {
            orig(self, ref gameTime);
        }
        finally
        {
            PerfData.AddUpdateCpu(PerfData.MsSince(start));
        }
    }

    private static void OnDoDraw(On_Main.orig_DoDraw orig, Main self, GameTime gameTime)
    {
        double frameMs = PerfData.OnDrawStart();
        long start = PerfData.Now;
        try
        {
            orig(self, gameTime);
        }
        finally
        {
            PerfData.AddDrawCpu(PerfData.MsSince(start));
            PerfData.EndFrame(frameMs);
        }
    }

    private static void OnRenderTiles(On_Main.orig_RenderTiles orig, Main self)
    {
        long start = PerfData.Now;
        try
        {
            orig(self);
        }
        finally
        {
            PerfData.AddTiles(PerfData.MsSince(start));
        }
    }

    private static void OnRenderTiles2(On_Main.orig_RenderTiles2 orig, Main self)
    {
        long start = PerfData.Now;
        try
        {
            orig(self);
        }
        finally
        {
            PerfData.AddTiles(PerfData.MsSince(start));
        }
    }

    private static void OnRenderWalls(On_Main.orig_RenderWalls orig, Main self)
    {
        long start = PerfData.Now;
        try
        {
            orig(self);
        }
        finally
        {
            PerfData.AddWalls(PerfData.MsSince(start));
        }
    }

    private static void OnRenderBackground(On_Main.orig_RenderBackground orig, Main self)
    {
        long start = PerfData.Now;
        try
        {
            orig(self);
        }
        finally
        {
            PerfData.AddBackground(PerfData.MsSince(start));
        }
    }

    private static void OnDrawDust(On_Main.orig_DrawDust orig, Main self)
    {
        long start = PerfData.Now;
        try
        {
            orig(self);
        }
        finally
        {
            PerfData.AddDust(PerfData.MsSince(start));
        }
    }

    private static void OnDrawProjectiles(On_Main.orig_DrawProjectiles orig, Main self)
    {
        long start = PerfData.Now;
        try
        {
            orig(self);
        }
        finally
        {
            PerfData.AddProjectiles(PerfData.MsSince(start));
        }
    }

    private static void OnDrawNpcs(On_Main.orig_DrawNPCs orig, Main self, bool behindTiles)
    {
        long start = PerfData.Now;
        try
        {
            orig(self, behindTiles);
        }
        finally
        {
            PerfData.AddNpcs(PerfData.MsSince(start));
        }
    }

    private static void OnDrawItems(On_Main.orig_DrawItems orig, Main self)
    {
        long start = PerfData.Now;
        try
        {
            orig(self);
        }
        finally
        {
            PerfData.AddItems(PerfData.MsSince(start));
        }
    }

    private static void OnLightTiles(On_Lighting.orig_LightTiles orig, int firstX, int lastX, int firstY, int lastY)
    {
        long start = PerfData.Now;
        try
        {
            orig(firstX, lastX, firstY, lastY);
        }
        finally
        {
            PerfData.AddLight(PerfData.MsSince(start));
        }
    }

    // Пока идёт параллельная фаза (gore/rain), AddLight зовут воркеры —
    // берём общий лок. В обычном коде это один volatile-read.
    private static void OnAddLight(On_Lighting.orig_AddLight_Vector2_float_float_float orig, Vector2 position, float r, float g, float b)
    {
        if (!ParallelEffects.Busy)
        {
            orig(position, r, g, b);
            return;
        }

        lock (ParallelEffects.Lock)
        {
            orig(position, r, g, b);
        }
    }

    private static int OnNewDust(On_Dust.orig_NewDust orig, Vector2 position, int width, int height, int type,
        float speedX, float speedY, int alpha, Color newColor, float scale)
    {
        if (!ParallelEffects.Busy)
            return orig(position, width, height, type, speedX, speedY, alpha, newColor, scale);

        lock (ParallelEffects.Lock)
        {
            return orig(position, width, height, type, speedX, speedY, alpha, newColor, scale);
        }
    }

    private static void OnGoreUpdate(On_Gore.orig_Update orig, Gore self)
    {
        if (GoreParallel.ShouldSkip(self))
            return;

        orig(self);
    }

    private static void OnRainUpdate(On_Rain.orig_Update orig, Rain self)
    {
        if (RainParallel.ShouldSkip(self))
            return;

        orig(self);
    }

    private static void OnDrawRain(On_Main.orig_DrawRain orig, Main self)
    {
        RainParallel.Begin();
        try
        {
            orig(self);
        }
        finally
        {
            RainParallel.End();
        }
    }

    // ---- отложенный автосейв ----

    private static void OnDoUpdateAutoSave(On_Main.orig_DoUpdate_AutoSave orig)
    {
        if (!RuntimeConfig.DeferAutosaveDuringBoss || Main.netMode != 0)
        {
            RunAutoSave(orig);
            return;
        }

        if (Main.gameMenu)
        {
            RunAutoSave(orig);
            return;
        }

        if (!BossActive())
        {
            _deferStartedAt = 0;
            RunAutoSave(orig);
            return;
        }

        long now = PerfData.Now;
        if (_deferStartedAt == 0)
        {
            _deferStartedAt = now;
            PerfData.AutosaveDeferrals++;
        }

        // Предохранитель: если «босс» остался активным навсегда, через
        // 10 минут автосейв всё равно сработает.
        if (PerfData.MsSince(_deferStartedAt) > 600000.0)
        {
            _deferStartedAt = 0;
            RunAutoSave(orig);
        }
    }

    private static void RunAutoSave(On_Main.orig_DoUpdate_AutoSave orig)
    {
        long start = PerfData.Now;
        try
        {
            orig();
        }
        finally
        {
            PerfData.NoteSave(PerfData.MsSince(start));
        }
    }

    private static void OnSectionTileFrameWithCheck(On_WorldGen.orig_SectionTileFrameWithCheck orig, int startX, int startY, int endX, int endY)
    {
        long start = PerfData.Now;
        orig(startX, startY, endX, endY);
        double ms = PerfData.MsSince(start);
        if (ms >= 0.25)
            PerfData.AddSectionFrame(ms);
    }

    // Босс в радиусе 8000 px от любого игрока-клиента. Используется и
    // отложенным автосейвом, и BossPerfMode — один проход на всех.
    // v0.8.6: результат кешируется на один тик обновления — автосейв
    // (DoUpdate_AutoSave) и BossPerfMode (PostUpdateEverything) зовут
    // это в пределах одного Update, второй вызов берёт кеш. На сервере
    // и в одиночной логика одна и та же.
    private static long _bossScanTick = -1;
    private static bool _bossScanResult;

    internal static bool BossActive()
    {
        long tick = Main.GameUpdateCount;
        if (tick == _bossScanTick)
            return _bossScanResult;

        _bossScanTick = tick;
        _bossScanResult = ScanBosses();
        return _bossScanResult;
    }

    private static bool ScanBosses()
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

    // ---- интерфейс ----

    public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
    {
        int index = layers.FindIndex(layer => layer.Name == "Vanilla: Mouse Text");
        if (index < 0)
            index = layers.Count;

        layers.Insert(index, new LegacyGameInterfaceLayer("EngineOptimizer: Profiler", DrawOverlay, InterfaceScaleType.UI));
        layers.Insert(index + 1, new LegacyGameInterfaceLayer("EngineOptimizer: World Preload", DrawPreloadBanner, InterfaceScaleType.UI));
    }

    private static bool DrawPreloadBanner()
    {
        if (!WorldPreloader.Active || Main.gameMenu || Main.dedServ)
            return true;

        var pixel = TextureAssets.MagicPixel.Value;
        int width = 540;
        int height = 62;
        int x = (Main.screenWidth - width) / 2;
        int y = 56;

        Main.spriteBatch.Draw(pixel, new Rectangle(x, y, width, height), new Color(10, 12, 16, 208));
        Main.spriteBatch.Draw(pixel, new Rectangle(x, y, 3, height), new Color(120, 220, 130));
        DrawBorder(pixel, x, y, width, height, new Color(255, 255, 255, 26));

        string headline = $"Preloading world  {WorldPreloader.Percent:0}%";
        Utils.DrawBorderString(Main.spriteBatch, headline, new Vector2(x + 16f, y + 8f), Color.White, 0.82f);

        // v1.0: подсказка, что прогрузку можно остановить клавишей.
        Utils.DrawBorderString(Main.spriteBatch, "F8 - cancel / toggle", new Vector2(x + 16f, y + 28f), new Color(150, 155, 165), 0.62f);

        string counter = $"{WorldPreloader.Done}/{WorldPreloader.Total} sections";
        double eta = WorldPreloader.EstimatedSecondsLeft;
        if (eta >= 3.0)
            counter = counter + $"  ~{eta:0} s";

        float counterWidth = FontAssets.MouseText.Value.MeasureString(counter).X * 0.7f;
        Utils.DrawBorderString(Main.spriteBatch, counter, new Vector2(x + width - 16f - counterWidth, y + 10f), new Color(150, 155, 165), 0.7f);

        int barWidth = width - 32;
        int filled = (int)(barWidth * WorldPreloader.Percent / 100.0);
        Main.spriteBatch.Draw(pixel, new Rectangle(x + 16, y + height - 18, barWidth, 7), new Color(255, 255, 255, 28));
        if (filled > 0)
            Main.spriteBatch.Draw(pixel, new Rectangle(x + 16, y + height - 18, filled, 7), new Color(120, 220, 130));

        return true;
    }

    private static bool DrawOverlay()
    {
        if (!PerfData.Visible || Main.gameMenu || Main.dedServ)
            return true;

        // Строки пересобираются 4 раза в секунду: минус сотни аллокаций.
        if (_overlayBuiltAt == 0 || PerfData.Now - _overlayBuiltAt >= Stopwatch.Frequency / 4)
            BuildOverlayLines();

        var pixel = TextureAssets.MagicPixel.Value;

        Main.spriteBatch.Draw(pixel, new Rectangle(12, 12, 524, 287), new Color(10, 12, 16, 208));
        Main.spriteBatch.Draw(pixel, new Rectangle(12, 12, 3, 287), _overlayFpsColor * 0.85f);
        DrawBorder(pixel, 12, 12, 524, 287, new Color(255, 255, 255, 26));

        Utils.DrawBorderString(Main.spriteBatch, "ENGINE OPTIMIZER", new Vector2(30f, 15f), new Color(108, 116, 130), 0.56f);
        Utils.DrawBorderString(Main.spriteBatch, _overlayKeyHint, new Vector2(502f, 15f), new Color(108, 116, 130), 0.56f);
        Utils.DrawBorderString(Main.spriteBatch, _overlayFps, new Vector2(30f, 27f), _overlayFpsColor, 1.35f);

        float fpsWidth = FontAssets.MouseText.Value.MeasureString(_overlayFps).X * 1.35f;
        Utils.DrawBorderString(Main.spriteBatch, "FPS", new Vector2(34f + fpsWidth, 38f), new Color(150, 155, 165), 0.62f);
        Utils.DrawBorderString(Main.spriteBatch, _overlayFrame, new Vector2(144f, 35f), _overlayFrameColor, 0.74f);
        Utils.DrawBorderString(Main.spriteBatch, _overlayGame, new Vector2(280f, 35f), new Color(150, 155, 165), 0.68f);

        Main.spriteBatch.Draw(pixel, new Rectangle(28, 62, 492, 1), new Color(255, 255, 255, 24));

        for (int i = 0; i < OverlayRows; i++)
        {
            float rowY = 70f + i * 19f;
            DrawStat(30f, rowY, 250f, OverlayLabelsLeft[i], OverlayValuesLeft[i], OverlayColorsLeft[i]);
            DrawStat(282f, rowY, 518f, OverlayLabelsRight[i], OverlayValuesRight[i], OverlayColorsRight[i]);
        }

        DrawGraph(pixel, 28, 251, 492, 38);

        return true;
    }

    private static void DrawBorder(Texture2D pixel, int x, int y, int width, int height, Color color)
    {
        Main.spriteBatch.Draw(pixel, new Rectangle(x, y, width, 1), color);
        Main.spriteBatch.Draw(pixel, new Rectangle(x, y + height - 1, width, 1), color);
        Main.spriteBatch.Draw(pixel, new Rectangle(x, y, 1, height), color);
        Main.spriteBatch.Draw(pixel, new Rectangle(x + width - 1, y, 1, height), color);
    }

    private static void DrawStat(float x, float y, float valueRight, string label, string value, Color valueColor)
    {
        Utils.DrawBorderString(Main.spriteBatch, label, new Vector2(x, y), new Color(142, 148, 160), 0.66f);
        float width = FontAssets.MouseText.Value.MeasureString(value).X * 0.72f;
        Utils.DrawBorderString(Main.spriteBatch, value, new Vector2(valueRight - width, y), valueColor, 0.72f);
    }

    private static void BuildOverlayLines()
    {
        double fps = PerfData.SmoothedFps;
        double cpu = PerfData.UpdateMs + PerfData.DrawMs;

        _overlayFps = $"{fps:0.0}";
        _overlayFpsColor = FpsColor(fps);
        _overlayFrame = $"frame {PerfData.FrameMs:0.00} ms";
        _overlayFrameColor = FrameColor(PerfData.FrameMs);
        _overlayGame = $"game {PerfData.GameFps}";
        _overlayKeyHint = KeyHint();

        Row(0, "cpu total", $"{cpu:0.0} ms", Color.White, true);
        Row(1, "update / draw", $"{PerfData.UpdateMs:0.00} / {PerfData.DrawMs:0.00}", Color.Gainsboro, true);
        Row(2, "present / other", $"{PerfData.UnaccountedMs:0.00} ms",
            PerfData.UnaccountedMs > 10.0 ? Color.Gold : Color.Gainsboro, true);
        Row(3, "GC / pause", $"{PerfData.Gc0PerSecond:0.0}/s  {PerfData.GcPauseMsPerSecond:0.0} ms/s",
            PerfData.GcPauseMsPerSecond > 40.0 ? Color.Orange : Color.Gainsboro, true);
        Row(4, "alloc", $"{PerfData.AllocMbPerSecond:0.0} MB/s", Color.Gainsboro, true);
        Row(5, "light / tiles", $"{PerfData.LightingMs:0.00} / {PerfData.TilesMs:0.00}", Color.Gainsboro, true);
        Row(6, "walls / bg", $"{PerfData.WallsMs:0.00} / {PerfData.BackgroundMs:0.00}", Color.Gainsboro, true);
        // v0.8.10: статус курсора, а не только миллисекунды — по нему
        // видно, жив ли аппаратный курсор (hw / vanilla / failed / off).
        Row(7, "cursor", $"{SmoothCursorSystem.Status}  {PerfData.CursorMs:0.00} ms",
            SmoothCursorSystem.Failed ? Color.OrangeRed : Color.Gainsboro, true);

        int spikes = Telemetry.SpikesLastMinute;
        int preloadSpikes = Telemetry.PreloadSpikesLastMinute;
        Row(0, "spikes 60s",
            preloadSpikes > 0 ? $"{spikes}  (+{preloadSpikes} preload)" : $"{spikes}",
            spikes > 5 ? Color.Gold : Color.Gainsboro, false);
        Row(1, "preload",
            WorldPreloader.UserDisabled
                ? "off (F8)"
                : WorldPreloader.Active
                    ? $"{WorldPreloader.Percent:0}%  {WorldPreloader.LastUpdateMs:0.0} ms"
                    : (WorldPreloader.FramedSections > 0 ? $"done {WorldPreloader.Seconds:0} s" : "-"),
            WorldPreloader.UserDisabled ? Color.OrangeRed : WorldPreloader.Active ? Color.Gold : Color.Gainsboro, false);
        Row(2, "autosave",
            PerfData.SaveCount > 0 ? $"{PerfData.SaveMs:0} ms  x{PerfData.SaveCount}" : "-",
            PerfData.SaveMs > 40.0 ? Color.Gold : Color.Gainsboro, false);
        Row(3, "section", $"{PerfData.SectionMs:0.0} ms", Color.Gainsboro, false);
        Row(4, "parallel gore", $"{PerfData.GoreParallelMs:0.00} ms / {PerfData.GoreParallelCount}",
            GoreParallel.Failed ? Color.OrangeRed : Color.Gainsboro, false);
        // Подсветка аномального числа снарядов (>150): в игру столько
        // одновременно почти не бывает — см. projectile_top в live.json.
        Row(5, "dust / proj / npc", $"{PerfData.DustCount} / {PerfData.ProjectileCount} / {PerfData.NpcCount}",
            PerfData.ProjectileCount > 150 ? Color.Gold : Color.Gainsboro, false);
        Row(6, "proc / threads", $"{Telemetry.ProcessCpuPct:0}%  {Telemetry.ThreadCount} thr", Color.Gainsboro, false);
        Row(7, "boss perf",
            BossPerfMode.Active ? "on (" + BossPerfMode.HookSummary() + ")" : (BossPerfMode.AnyHook ? "ready" : "-"),
            BossPerfMode.Active ? Color.Gold : Color.Gainsboro, false);
        // v1.1: глобальный пресет (F9).
        Row(8, "preset", RuntimeConfig.PresetName,
            RuntimeConfig.Preset == OptimizerPreset.Balanced ? Color.Gainsboro : Color.SkyBlue, false);

        _overlayBuiltAt = PerfData.Now;
    }

    // v0.8.7: реальная клавиша через публичный API ModKeybind.GetAssignedKeys
    // (раньше лезли рефлексией в PlayerInput.CurrentProfile и рисковали
    // промахнуться по формату имени кейбинда). Длинные списки режем —
    // подсказка обязана влезать в угол панели.
    private static string KeyHint()
    {
        try
        {
            var keys = ToggleKey?.GetAssignedKeys();
            if (keys != null && keys.Count > 0)
            {
                string hint = string.Join("/", keys);
                if (hint.Length > 12)
                    hint = hint.Substring(0, 12);
                return hint;
            }

            return "-";
        }
        catch
        {
            return "F6";
        }
    }

    private static void Row(int index, string label, string value, Color color, bool left)
    {
        if (left)
        {
            OverlayLabelsLeft[index] = label;
            OverlayValuesLeft[index] = value;
            OverlayColorsLeft[index] = color;
        }
        else
        {
            OverlayLabelsRight[index] = label;
            OverlayValuesRight[index] = value;
            OverlayColorsRight[index] = color;
        }
    }

    private static double FrameBudgetMs()
    {
        int rate = Main.frameRate > 0 ? Main.frameRate : 60;
        return 1000.0 / Math.Clamp(rate, 30, 240);
    }

    private static Color FpsColor(double fps)
    {
        double target = 1000.0 / FrameBudgetMs();
        if (fps >= target * 0.95)
            return Color.LimeGreen;
        if (fps >= target * 0.75)
            return Color.Gold;
        return Color.OrangeRed;
    }

    private static Color FrameColor(double ms)
    {
        double budget = FrameBudgetMs();
        if (ms <= budget * 1.1)
            return Color.LimeGreen;
        if (ms <= budget * 1.75)
            return Color.Gold;
        return Color.OrangeRed;
    }

    // Мини-график истории кадра (2 секунды) с линией бюджета.
    private static void DrawGraph(Texture2D pixel, int x, int y, int width, int height)
    {
        int count = PerfData.HistoryCount;
        float scale = height / 40f;
        int rate = Main.frameRate > 0 ? Main.frameRate : 60;
        float targetMs = 1000f / Math.Clamp(rate, 30, 240);

        Main.spriteBatch.Draw(pixel, new Rectangle(x, y, width, height), new Color(255, 255, 255, 10));

        int slot = Math.Max(2, height / 120);
        int barWidth = Math.Max(1, slot - 1);

        for (int i = 0; i < count; i++)
        {
            int index = (PerfData.HistoryHead - count + i + 2 * PerfData.HistoryLength) % PerfData.HistoryLength;
            float ms = (float)PerfData.HistoryMs[index];
            int barHeight = Math.Clamp((int)(ms * scale), 1, height);
            Color color = ms > targetMs * 1.75f ? Color.OrangeRed
                : ms > targetMs * 1.1f ? Color.Gold
                : Color.LimeGreen;
            Main.spriteBatch.Draw(pixel,
                new Rectangle(x + i * slot, y + height - barHeight, barWidth, barHeight),
                color * 0.75f);
        }

        int budgetY = y + height - (int)(targetMs * scale);
        Main.spriteBatch.Draw(pixel, new Rectangle(x, budgetY, width, 1), new Color(255, 255, 255, 90));
    }
}
