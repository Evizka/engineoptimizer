using System;
using System.Diagnostics;
using Terraria;

namespace EngineOptimizer.Perf;

// Срез секций тика (миллисекунды update-фаз) для последнего кадра.
public struct FrameSections
{
    public double Players;
    public double Npcs;
    public double Gores;
    public double Projectiles;
    public double Items;
    public double Dust;
    public double Time;
    public double World;
    public double Invasions;
}

// Все счётчики кадра: тайминги фаз update/draw, секций тика, GC, сущностей.
// Поля с подчёркиванием — накопители текущего кадра (сбрасываются каждый кадр),
// публичные — EMA-сглаженные значения для оверлея и телеметрии.
public static class PerfData
{
    private const double TargetFrameMs = 1000.0 / 60.0;
    private const double SpikeThresholdMs = 25.0;
    public const int HistoryLength = 120;

    public static bool Visible = true;
    public static bool SaveReport = true;

    private static long _lastDrawTs;
    private static long _lastUpdateTs;

    // накопители кадра
    public static double UpdateCpuMs;
    private static double _drawCpuMs;
    private static double _lightMs;
    private static double _tilesMs;
    private static double _wallsMs;
    private static double _backgroundMs;
    private static double _dustMs;
    private static double _projectilesMs;
    private static double _npcMs;
    private static double _itemsMs;
    private static double _updPlayersMs;
    private static double _updNpcsMs;
    private static double _updGoresMs;
    private static double _updProjectilesMs;
    private static double _updItemsMs;
    private static double _updDustMs;
    private static double _updTimeMs;
    private static double _updWorldMs;
    private static double _updInvasionsMs;
    private static double _goreParMs;
    private static double _rainParMs;
    private static int _goreParCount;
    private static int _rainParCount;
    private static double _cursorMs;
    private static double _sectionMs;
    private static int _sectionCount;
    private static double _saveMsThisFrame;
    private static TimeSpan _lastPauseTotal;
    private static long _pauseSampleTs;
    private static int _fpsFrames;
    private static long _fpsWindowTs;
    private static long _lastGcTs;
    private static int _lastGc0;
    private static int _lastGc2;
    private static TimeSpan _lastGcPause;
    private static long _lastAllocBytes;
    private static int _countdown;

    // EMA-сглаженные значения
    public static double FrameMs = TargetFrameMs;
    public static double UpdateMs;
    public static double DrawMs;
    public static double LightingMs;
    public static double TilesMs;
    public static double WallsMs;
    public static double BackgroundMs;
    public static double DustMs;
    public static double ProjectilesMs;
    public static double NpcsMs;
    public static double ItemsMs;
    public static double UpdPlayersMs;
    public static double UpdNpcsMs;
    public static double UpdGoresMs;
    public static double UpdProjectilesMs;
    public static double UpdItemsMs;
    public static double UpdDustMs;
    public static double UpdTimeMs;
    public static double UpdWorldMs;
    public static double UpdInvasionsMs;
    public static double GoreParallelMs;
    public static int GoreParallelCount;
    public static long GoreParallelTotal;
    public static double RainParallelMs;
    public static int RainParallelCount;
    public static long RainParallelTotal;
    public static double CursorMs;
    public static double SaveThisFrameMs;
    public static double SaveMs;
    public static int SaveCount;
    public static double GcPauseThisFrameMs;
    public static double UnaccountedMs;
    public static double PreloadMsThisFrame;
    public static double SectionMs;
    public static int SectionCount;
    public static FrameSections LastSections;
    public static double DrawHz = 60.0;
    public static double UpdateHz = 60.0;
    public static int GameFps;
    public static double FpsCount;
    public static double Gc0PerSecond;
    public static double Gc2PerSecond;
    public static double GcPauseMsPerSecond;
    public static double AllocMbPerSecond;
    public static int DustCount;
    public static int ProjectileCount;
    public static int NpcCount;
    public static int ItemCount;
    public static int AutosaveDeferrals;

    public static readonly double[] HistoryMs = new double[HistoryLength];
    public static int HistoryCount;
    public static int HistoryHead;

    public static double LastFrameRawMs;
    public static double LastUpdateRawMs;
    public static double LastDrawRawMs;
    public static double LastLightRawMs;

    public static double SmoothedFps => FpsCount > 0.0 ? FpsCount : DrawHz;

    public static long Now => Stopwatch.GetTimestamp();

    private static void PauseSampleReset()
    {
        _pauseSampleTs = 0;
        _lastPauseTotal = default;
    }

    // ms, прошедшие с метки времени ts (Stopwatch-тики).
    public static double MsSince(long ts) =>
        (Now - ts) * 1000.0 / Stopwatch.Frequency;

    private static double MsFromTicks(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;

    private static double Ema(double current, double sample, double weight) =>
        current + (sample - current) * weight;

    public static void OnUpdateStart()
    {
        long now = Now;

        if (_lastUpdateTs != 0)
        {
            double dt = MsFromTicks(now - _lastUpdateTs);
            if (dt >= 2.0 && dt <= 500.0)
                UpdateHz = Ema(UpdateHz, 1000.0 / dt, 0.1);
        }

        _lastUpdateTs = now;
    }

    // Возвращает длительность предыдущего кадра (по моментам начала draw) или 0.
    public static double OnDrawStart()
    {
        long now = Now;
        double frameMs = 0;

        if (_lastDrawTs != 0)
        {
            double dt = MsFromTicks(now - _lastDrawTs);
            if (dt >= 1.0 && dt <= 500.0)
                frameMs = dt;
        }

        _lastDrawTs = now;
        return frameMs;
    }

    public static void AddUpdateCpu(double ms) => UpdateCpuMs += ms;

    // Заметить автосейв: кадры с сохранением отмечаются отдельно.
    public static void NoteSave(double ms)
    {
        if (ms < 1.0)
            return;

        _saveMsThisFrame += ms;
        SaveCount++;
        SaveMs = Ema(SaveMs, ms, 0.2);
    }

    public static void AddDrawCpu(double ms) => _drawCpuMs += ms;
    public static void AddLight(double ms) => _lightMs += ms;
    public static void AddTiles(double ms) => _tilesMs += ms;
    public static void AddWalls(double ms) => _wallsMs += ms;
    public static void AddBackground(double ms) => _backgroundMs += ms;
    public static void AddDust(double ms) => _dustMs += ms;
    public static void AddProjectiles(double ms) => _projectilesMs += ms;
    public static void AddNpcs(double ms) => _npcMs += ms;
    public static void AddItems(double ms) => _itemsMs += ms;
    public static void AddCursor(double ms) => _cursorMs += ms;
    public static void AddUpdPlayers(double ms) => _updPlayersMs += ms;
    public static void AddUpdNpcs(double ms) => _updNpcsMs += ms;
    public static void AddUpdGores(double ms) => _updGoresMs += ms;
    public static void AddUpdProjectiles(double ms) => _updProjectilesMs += ms;
    public static void AddUpdItems(double ms) => _updItemsMs += ms;
    public static void AddUpdDust(double ms) => _updDustMs += ms;
    public static void AddUpdTime(double ms) => _updTimeMs += ms;
    public static void AddUpdWorld(double ms) => _updWorldMs += ms;
    public static void AddUpdInvasions(double ms) => _updInvasionsMs += ms;

    public static void AddSectionFrame(double ms)
    {
        _sectionMs += ms;
        _sectionCount++;
    }

    public static void SetGoreParallel(double ms, int count)
    {
        _goreParMs = ms;
        _goreParCount = count;
        if (count > 0)
            GoreParallelTotal++;
    }

    public static void SetRainParallel(double ms, int count)
    {
        _rainParMs = ms;
        _rainParCount = count;
        if (count > 0)
            RainParallelTotal++;
    }

    // Закрытие кадра: обновить EMA, FPS, историю, телеметрию и GC-метрики.
    public static void EndFrame(double frameMs)
    {
        // Аномально длинный кадр ( alt-tab / загрузка ) не портит статистику.
        if (frameMs > 500.0)
        {
            ResetAccumulators();
            return;
        }

        long now = Now;

        PreloadMsThisFrame = WorldPreloader.LastUpdateMs;
        SaveThisFrameMs = _saveMsThisFrame;

        TimeSpan pauseTotal = GC.GetTotalPauseDuration();
        if (_pauseSampleTs != 0)
            GcPauseThisFrameMs = (pauseTotal - _lastPauseTotal).TotalMilliseconds;
        else
            GcPauseThisFrameMs = 0.0;
        _lastPauseTotal = pauseTotal;
        _pauseSampleTs = now;

        if (frameMs <= 0.0)
        {
            ResetAccumulators();
            return;
        }

        // честный fps_draw: число кадров за последнюю целую секунду
        _fpsFrames++;
        if (_fpsWindowTs == 0)
        {
            _fpsWindowTs = now;
        }
        else
        {
            double fpsSeconds = (now - _fpsWindowTs) / (double)Stopwatch.Frequency;
            if (fpsSeconds >= 1.0)
            {
                FpsCount = _fpsFrames / fpsSeconds;
                _fpsFrames = 0;
                _fpsWindowTs = now;
            }
        }

        LastFrameRawMs = frameMs;
        LastUpdateRawMs = UpdateCpuMs;
        LastDrawRawMs = _drawCpuMs;
        LastLightRawMs = _lightMs;

        FrameMs = Ema(FrameMs, frameMs, 0.12);
        DrawHz = Ema(DrawHz, 1000.0 / Math.Max(0.1, frameMs), 0.1);
        UpdateMs = Ema(UpdateMs, UpdateCpuMs, 0.12);
        DrawMs = Ema(DrawMs, _drawCpuMs, 0.12);
        LightingMs = Ema(LightingMs, _lightMs, 0.12);
        TilesMs = Ema(TilesMs, _tilesMs, 0.12);
        WallsMs = Ema(WallsMs, _wallsMs, 0.12);
        BackgroundMs = Ema(BackgroundMs, _backgroundMs, 0.12);
        DustMs = Ema(DustMs, _dustMs, 0.12);
        ProjectilesMs = Ema(ProjectilesMs, _projectilesMs, 0.12);
        NpcsMs = Ema(NpcsMs, _npcMs, 0.12);
        ItemsMs = Ema(ItemsMs, _itemsMs, 0.12);
        CursorMs = Ema(CursorMs, _cursorMs, 0.12);
        UnaccountedMs = Ema(UnaccountedMs, Math.Max(0.0, frameMs - UpdateCpuMs - _drawCpuMs), 0.12);
        UpdPlayersMs = Ema(UpdPlayersMs, _updPlayersMs, 0.12);
        UpdNpcsMs = Ema(UpdNpcsMs, _updNpcsMs, 0.12);
        UpdGoresMs = Ema(UpdGoresMs, _updGoresMs, 0.12);
        UpdProjectilesMs = Ema(UpdProjectilesMs, _updProjectilesMs, 0.12);
        UpdItemsMs = Ema(UpdItemsMs, _updItemsMs, 0.12);
        UpdDustMs = Ema(UpdDustMs, _updDustMs, 0.12);
        UpdTimeMs = Ema(UpdTimeMs, _updTimeMs, 0.12);
        UpdWorldMs = Ema(UpdWorldMs, _updWorldMs, 0.12);
        UpdInvasionsMs = Ema(UpdInvasionsMs, _updInvasionsMs, 0.12);
        GoreParallelMs = Ema(GoreParallelMs, _goreParMs, 0.12);
        GoreParallelCount = _goreParCount;
        RainParallelMs = Ema(RainParallelMs, _rainParMs, 0.12);
        RainParallelCount = _rainParCount;
        SectionMs = Ema(SectionMs, _sectionMs, 0.12);
        SectionCount = _sectionCount;

        LastSections = new FrameSections
        {
            Players = _updPlayersMs,
            Npcs = _updNpcsMs,
            Gores = _updGoresMs,
            Projectiles = _updProjectilesMs,
            Items = _updItemsMs,
            Dust = _updDustMs,
            Time = _updTimeMs,
            World = _updWorldMs,
            Invasions = _updInvasionsMs
        };

        GameFps = Main.frameRate;

        HistoryMs[HistoryHead] = frameMs;
        HistoryHead = (HistoryHead + 1) % HistoryLength;
        if (HistoryCount < HistoryLength)
            HistoryCount++;

        PerfLog.Record(frameMs, UpdateCpuMs, _drawCpuMs, _lightMs);

        Telemetry.RecordFrame(
            frameMs, UpdateCpuMs, _drawCpuMs, _lightMs,
            _tilesMs + _wallsMs, _backgroundMs, _dustMs,
            _projectilesMs, _npcMs, _itemsMs);

        SampleGc();

        if (--_countdown <= 0)
        {
            _countdown = 30;
            CountEntities();
        }

        ResetAccumulators();
    }

    private static void ResetAccumulators()
    {
        UpdateCpuMs = 0;
        _drawCpuMs = 0;
        _lightMs = 0;
        _tilesMs = 0;
        _wallsMs = 0;
        _backgroundMs = 0;
        _dustMs = 0;
        _projectilesMs = 0;
        _npcMs = 0;
        _itemsMs = 0;
        _updPlayersMs = 0;
        _updNpcsMs = 0;
        _updGoresMs = 0;
        _updProjectilesMs = 0;
        _updItemsMs = 0;
        _updDustMs = 0;
        _updTimeMs = 0;
        _updWorldMs = 0;
        _updInvasionsMs = 0;
        _goreParMs = 0;
        _goreParCount = 0;
        _rainParMs = 0;
        _rainParCount = 0;
        _cursorMs = 0;
        _sectionMs = 0;
        _sectionCount = 0;
        _saveMsThisFrame = 0;
        SaveThisFrameMs = 0;
        GcPauseThisFrameMs = 0;
    }

    // Полный сброс при входе в мир.
    public static void ResetAll()
    {
        _lastDrawTs = 0;
        _lastUpdateTs = 0;
        _lastGcTs = 0;
        _lastAllocBytes = 0;
        _lastGc0 = 0;
        _countdown = 0;

        UpdateCpuMs = 0;
        _drawCpuMs = 0;
        _lightMs = 0;
        _tilesMs = 0;
        _wallsMs = 0;
        _backgroundMs = 0;
        _dustMs = 0;
        _projectilesMs = 0;
        _npcMs = 0;
        _itemsMs = 0;
        _updPlayersMs = 0;
        _updNpcsMs = 0;
        _updGoresMs = 0;
        _updProjectilesMs = 0;
        _updItemsMs = 0;
        _updDustMs = 0;
        _updTimeMs = 0;
        _updWorldMs = 0;
        _updInvasionsMs = 0;
        _goreParMs = 0;
        _goreParCount = 0;
        _rainParMs = 0;
        _rainParCount = 0;
        _cursorMs = 0;
        _sectionMs = 0;
        _sectionCount = 0;

        FrameMs = TargetFrameMs;
        UpdateMs = 0;
        DrawMs = 0;
        LightingMs = 0;
        TilesMs = 0;
        WallsMs = 0;
        BackgroundMs = 0;
        DustMs = 0;
        ProjectilesMs = 0;
        NpcsMs = 0;
        ItemsMs = 0;
        UpdPlayersMs = 0;
        UpdNpcsMs = 0;
        UpdGoresMs = 0;
        UpdProjectilesMs = 0;
        UpdItemsMs = 0;
        UpdDustMs = 0;
        UpdTimeMs = 0;
        UpdWorldMs = 0;
        UpdInvasionsMs = 0;
        GoreParallelMs = 0;
        GoreParallelCount = 0;
        GoreParallelTotal = 0;
        RainParallelMs = 0;
        RainParallelCount = 0;
        RainParallelTotal = 0;
        CursorMs = 0;
        SectionMs = 0;
        SectionCount = 0;
        LastSections = default;

        DrawHz = 60.0;
        UpdateHz = 60.0;
        GameFps = 0;
        FpsCount = 0;
        _fpsFrames = 0;
        _fpsWindowTs = 0;

        SaveMs = 0;
        SaveCount = 0;
        SaveThisFrameMs = 0;
        _saveMsThisFrame = 0;
        GcPauseThisFrameMs = 0;
        PauseSampleReset();

        PreloadMsThisFrame = 0;
        UnaccountedMs = 0;

        HistoryCount = 0;
        HistoryHead = 0;
        Array.Clear(HistoryMs, 0, HistoryMs.Length);

        Gc0PerSecond = 0;
        Gc2PerSecond = 0;
        GcPauseMsPerSecond = 0;
        AllocMbPerSecond = 0;

        AutosaveDeferrals = 0;
    }

    private static void SampleGc()
    {
        long now = Now;

        if (_lastGcTs == 0)
        {
            _lastGcTs = now;
            _lastGc0 = GC.CollectionCount(0);
            _lastGc2 = GC.CollectionCount(2);
            _lastGcPause = GC.GetTotalPauseDuration();
            _lastAllocBytes = GC.GetTotalAllocatedBytes(false);
            return;
        }

        double seconds = (now - _lastGcTs) / (double)Stopwatch.Frequency;
        if (seconds < 1.0)
            return;

        int gc0 = GC.CollectionCount(0);
        int gc2 = GC.CollectionCount(2);
        TimeSpan pause = GC.GetTotalPauseDuration();
        long alloc = GC.GetTotalAllocatedBytes(false);

        Gc0PerSecond = (gc0 - _lastGc0) / seconds;
        Gc2PerSecond = (gc2 - _lastGc2) / seconds;
        GcPauseMsPerSecond = (pause - _lastGcPause).TotalMilliseconds / seconds;
        AllocMbPerSecond = (alloc - _lastAllocBytes) / 1048576.0 / seconds;

        _lastGcTs = now;
        _lastGc0 = gc0;
        _lastGc2 = gc2;
        _lastGcPause = pause;
        _lastAllocBytes = alloc;
    }

    private static void CountEntities()
    {
        var dust = Main.dust;
        if (dust != null)
        {
            int count = 0;
            for (int i = 0; i < dust.Length; i++)
            {
                if (dust[i].active)
                    count++;
            }
            DustCount = count;
        }

        var projectiles = Main.projectile;
        if (projectiles != null)
        {
            int count = 0;
            for (int i = 0; i < projectiles.Length; i++)
            {
                if (projectiles[i].active)
                    count++;
            }
            ProjectileCount = count;
        }

        var npcs = Main.npc;
        if (npcs != null)
        {
            int count = 0;
            for (int i = 0; i < npcs.Length; i++)
            {
                if (npcs[i].active)
                    count++;
            }
            NpcCount = count;
        }

        var items = Main.item;
        if (items != null)
        {
            int count = 0;
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].active)
                    count++;
            }
            ItemCount = count;
        }
    }
}
