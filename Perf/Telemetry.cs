using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Terraria;
using Terraria.ModLoader;

namespace EngineOptimizer.Perf;

// Телеметрия: live.json (раз в секунду), timeline.csv (раз в 5 секунд),
// spikes.csv (каждый кадр >25 мс с причиной) и session.json при входе в мир.
// Текст собирается на игровом потоке, на диск пишет AsyncFileWriter.
public static class Telemetry
{
    private const int WindowSize = 600;
    private const int SpikeLiveCount = 10;
    private const int SpikeBufferLimit = 5000;
    private const double SpikeThresholdMs = 25.0;
    private const double FrameIgnoreMs = 500.0;

    private const string SpikeHeader =
        "time,frame_ms,cpu_update,cpu_draw,light,tiles_walls,bg,dust_ms,proj_ms,npc_ms,items_ms,fps_draw,gc0_s,alloc_mb_s,dust,proj,npc,items,bosses,biome,x,y,vx,vy,hp,dead,gc2_s,gc_pause_ms_s,upd_npc,upd_proj,upd_gore,upd_dust,gore_par_ms,gore_par_n,rain_par_ms,rain_par_n,preload_ms,cause,unacc_ms";

    private const string TimelineHeader =
        "time,fps_draw,fps_game,ups,frame_avg,frame_p95,frame_max,cpu_update,cpu_draw,light,tiles_walls,bg,dust_ms,proj_ms,npc_ms,items_ms,gc0_s,alloc_mb_s,dust,proj,npc,items,bosses,biome,x,y,gc2_s,gc_pause_ms_s,upd_players,upd_npcs,upd_gores,upd_proj,upd_items,upd_dust,upd_time,upd_world,upd_inv,gore_par_ms,gore_par_n,rain_par_ms,rain_par_n,proc_cpu_pct,threads,section_ms,section_n,preload_pct,cursor_ms,preload_ms";

    private static readonly double[] Window = new double[WindowSize];
    private static readonly double[] WindowCopy = new double[WindowSize];
    private static int _windowCount;
    private static int _windowHead;

    private static readonly List<Spike> Spikes = new List<Spike>(64);
    private static readonly List<string> SpikeCsv = new List<string>(256);
    private static readonly List<long> SpikeTimes = new List<long>(64);
    private static readonly List<string> SpikeCauses = new List<string>(64);

    private static long _lastWriteTs;
    private static long _lastTimelineTs;
    private static long _sessionStartTs;

    private static string _dir;
    private static Process _proc;
    private static TimeSpan _lastProcCpu;
    private static long _lastProcSampleTs;

    public static double ProcessCpuPct;
    public static int ThreadCount;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    // Один спайк — кадр дольше 25 мс со всем контекстом.
    private sealed class Spike
    {
        public DateTime At;
        public double FrameMs;
        public string Cause = "other";
        public double UpdateMs;
        public double DrawMs;
        public double LightMs;
        public double TilesWallsMs;
        public double BackgroundMs;
        public double DustMs;
        public double ProjectilesMs;
        public double NpcMs;
        public double ItemsMs;
        public double UpdNpcMs;
        public double UpdProjMs;
        public double UpdGoreMs;
        public double UpdDustMs;
        public double GoreParMs;
        public int GoreParN;
        public double RainParMs;
        public int RainParN;
        public double PreloadMs;
        public double Fps;
        public double Gc0;
        public double Gc2;
        public double GcPause;
        public double AllocMb;
        public int DustCount;
        public int ProjectileCount;
        public int NpcCount;
        public int ItemCount;
        public string Bosses;
        public string Biome;
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public int Hp;
        public int HpMax;
        public bool Dead;
    }

    public static void Reset()
    {
        _windowCount = 0;
        _windowHead = 0;
        Array.Clear(Window, 0, Window.Length);
        Spikes.Clear();
        SpikeCsv.Clear();
        SpikeTimes.Clear();
        SpikeCauses.Clear();
        _lastWriteTs = 0;
        _lastTimelineTs = 0;
        _sessionStartTs = PerfData.Now;
        _lastProcCpu = default;
        _lastProcSampleTs = 0;
        ProcessCpuPct = 0;
        ThreadCount = 0;

        try
        {
            AsyncFileWriter.Start();
            AsyncFileWriter.Flush(1000);
            Directory.CreateDirectory(Dir);
        }
        catch
        {
        }

        Rotate("timeline");
        Rotate("spikes");
    }

    private static string Dir =>
        _dir ??= Path.Combine(Main.SavePath, "EngineOptimizer");

    // Архивируем старый csv под именем с датой.
    private static void Rotate(string name)
    {
        try
        {
            string path = Path.Combine(Dir, name + ".csv");
            if (!File.Exists(path))
                return;

            if (new FileInfo(path).Length == 0)
            {
                File.Delete(path);
                return;
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv);
            string dest = Path.Combine(Dir, name + "-" + stamp + ".csv");

            int suffix = 1;
            while (File.Exists(dest))
                dest = Path.Combine(Dir, name + "-" + stamp + "-" + suffix++ + ".csv");

            File.Move(path, dest);
            TrimArchives(name);
        }
        catch
        {
        }
    }

    // Храним только последние 8 архивов каждого вида.
    private static void TrimArchives(string name)
    {
        try
        {
            var files = Directory.GetFiles(Dir, name + "-*.csv");
            if (files.Length <= 8)
                return;

            Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)));

            for (int i = 0; i < files.Length - 8; i++)
                File.Delete(files[i]);
        }
        catch
        {
        }
    }

    public static void Flush(int timeoutMs) =>
        AsyncFileWriter.Flush(timeoutMs);

    // session.json: окружение, настройки мода, список модов.
    public static void WriteSession()
    {
        try
        {
            Directory.CreateDirectory(Dir);

            var player = Main.LocalPlayer;
            var sb = new StringBuilder(2048);
            sb.Append('{');
            Str(sb, "started", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv));
            Str(sb, "terraria", Main.versionNumber);
            Str(sb, "tmodloader", typeof(ModLoader).Assembly.GetName().Version?.ToString() ?? "?");
            Str(sb, "runtime", Environment.Version.ToString());
            Num(sb, "cores", Environment.ProcessorCount);
            Bool(sb, "gc_server", System.Runtime.GCSettings.IsServerGC);
            Str(sb, "gc_latency", System.Runtime.GCSettings.LatencyMode.ToString());
            Num(sb, "gc_heap_mb", GC.GetTotalMemory(false) / 1048576.0);
            Bool(sb, "reduce_gc_pauses", OptimizerConfig.Instance?.ReduceGcPauses == true);
            Bool(sb, "defer_autosave_boss", OptimizerConfig.Instance?.DeferAutosaveDuringBoss == true);
            Bool(sb, "parallel_gore", OptimizerConfig.Instance?.ParallelGore == true);
            Bool(sb, "parallel_rain", OptimizerConfig.Instance?.ParallelRain == true);
            Bool(sb, "preload_world", OptimizerConfig.Instance?.PreloadWorld == true);
            Num(sb, "preload_budget_ms", OptimizerConfig.Instance?.PreloadBudgetMs ?? 0);
            Bool(sb, "smooth_cursor", OptimizerConfig.Instance?.SmoothHardwareCursor == true);
            Str(sb, "world", Main.worldName);
            Str(sb, "player", player != null ? player.name ?? "?" : null);
            Num(sb, "screen_width", Main.screenWidth);
            Num(sb, "screen_height", Main.screenHeight);
            Bool(sb, "vsync", Main.graphics != null && Main.graphics.SynchronizeWithVerticalRetrace);
            Bool(sb, "fixed_timestep", Main.instance != null && Main.instance.IsFixedTimeStep);
            Str(sb, "frame_skip", Main.FrameSkipMode.ToString());
            Str(sb, "lighting", Lighting.Mode.ToString());
            Num(sb, "world_surface", Main.worldSurface);
            Num(sb, "rock_layer", Main.rockLayer);

            sb.Append("\"mods\":[");
            var mods = ModLoader.Mods;
            for (int i = 0; i < mods.Length; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append('"')
                  .Append(Escape(mods[i].Name + " " + mods[i].Version?.ToString()))
                  .Append('"');
            }
            sb.Append(']').Append('}');

            File.WriteAllText(Path.Combine(Dir, "session.json"), sb.ToString(), Utf8);
        }
        catch
        {
        }
    }

    public static void RecordFrame(double frameMs, double updateCpu, double drawCpu, double light,
        double tilesWalls, double background, double dust, double projectiles, double npcs, double items)
    {
        try
        {
            RecordFrameCore(frameMs, updateCpu, drawCpu, light, tilesWalls, background, dust, projectiles, npcs, items);
        }
        catch
        {
        }
    }

    private static void RecordFrameCore(double frameMs, double updateCpu, double drawCpu, double light,
        double tilesWalls, double background, double dust, double projectiles, double npcs, double items)
    {
        if (frameMs <= 0.0 || frameMs > FrameIgnoreMs)
            return;

        Window[_windowHead] = frameMs;
        _windowHead = (_windowHead + 1) % WindowSize;
        if (_windowCount < WindowSize)
            _windowCount++;

        if (frameMs > SpikeThresholdMs)
            AddSpike(frameMs, updateCpu, drawCpu, light, tilesWalls, background, dust, projectiles, npcs, items);

        long now = PerfData.Now;
        if (_lastWriteTs == 0)
        {
            _lastWriteTs = now;
            _lastTimelineTs = now;
            return;
        }

        if ((now - _lastWriteTs) / (double)Stopwatch.Frequency < 1.0)
            return;

        _lastWriteTs = now;
        WriteLive();
        FlushSpikes();

        if ((now - _lastTimelineTs) / (double)Stopwatch.Frequency >= 5.0)
        {
            _lastTimelineTs = now;
            AppendTimeline();
        }
    }

    private static void AddSpike(double frameMs, double updateCpu, double drawCpu, double light,
        double tilesWalls, double background, double dust, double projectiles, double npcs, double items)
    {
        var player = Main.LocalPlayer;

        var spike = new Spike
        {
            At = DateTime.Now,
            FrameMs = frameMs,
            UpdateMs = updateCpu,
            DrawMs = drawCpu,
            LightMs = light,
            TilesWallsMs = tilesWalls,
            BackgroundMs = background,
            DustMs = dust,
            ProjectilesMs = projectiles,
            NpcMs = npcs,
            ItemsMs = items,
            PreloadMs = PerfData.PreloadMsThisFrame,
            Cause = ClassifySpike(frameMs, updateCpu, drawCpu),
            UpdNpcMs = PerfData.LastSections.Npcs,
            UpdProjMs = PerfData.LastSections.Projectiles,
            UpdGoreMs = PerfData.LastSections.Gores,
            UpdDustMs = PerfData.LastSections.Dust,
            GoreParMs = PerfData.GoreParallelMs,
            GoreParN = PerfData.GoreParallelCount,
            RainParMs = PerfData.RainParallelMs,
            RainParN = PerfData.RainParallelCount,
            Fps = PerfData.DrawHz,
            Gc0 = PerfData.Gc0PerSecond,
            Gc2 = PerfData.Gc2PerSecond,
            GcPause = PerfData.GcPauseMsPerSecond,
            AllocMb = PerfData.AllocMbPerSecond,
            DustCount = PerfData.DustCount,
            ProjectileCount = PerfData.ProjectileCount,
            NpcCount = PerfData.NpcCount,
            ItemCount = PerfData.ItemCount,
            Bosses = Bosses(),
            Biome = player != null ? Biome(player) : "?",
            X = player != null ? player.Center.X / 16f : 0f,
            Y = player != null ? player.Center.Y / 16f : 0f,
            Vx = player != null ? player.velocity.X : 0f,
            Vy = player != null ? player.velocity.Y : 0f,
            Hp = player != null ? player.statLife : 0,
            HpMax = player != null ? player.statLifeMax2 : 0,
            Dead = player != null && player.dead
        };

        Spikes.Add(spike);
        if (Spikes.Count > 256)
            Spikes.RemoveAt(0);

        SpikeTimes.Add(PerfData.Now);
        SpikeCauses.Add(spike.Cause);

        if (SpikeTimes.Count > 512)
        {
            SpikeTimes.RemoveRange(0, SpikeTimes.Count - 512);
            SpikeCauses.RemoveRange(0, SpikeCauses.Count - 512);
        }

        if (SpikeCsv.Count < SpikeBufferLimit)
            SpikeCsv.Add(ToCsv(spike));
    }

    private static void SampleProcess()
    {
        try
        {
            if (_proc == null)
                _proc = Process.GetCurrentProcess();

            var cpu = _proc.TotalProcessorTime;
            long now = PerfData.Now;

            if (_lastProcSampleTs != 0)
            {
                double wall = (now - _lastProcSampleTs) / (double)Stopwatch.Frequency;
                if (wall > 0.2)
                {
                    double pct = (cpu - _lastProcCpu).TotalSeconds / wall * 100.0;
                    if (pct < 0.0)
                        pct = 0.0;

                    ProcessCpuPct = ProcessCpuPct > 0.0
                        ? ProcessCpuPct * 0.7 + pct * 0.3
                        : pct;

                    _lastProcCpu = cpu;
                    _lastProcSampleTs = now;
                    ThreadCount = _proc.Threads.Count;
                }
            }
        }
        catch
        {
        }
    }

    private static void WriteLive()
    {
        if (_sessionStartTs == 0)
            _sessionStartTs = PerfData.Now;

        double avg = AvgWindow();
        double p95 = PercentileWindow(0.95);
        double max = MaxWindow();
        var player = Main.LocalPlayer;
        int spikesLastMinute = SpikesLastMinute;
        int preloadSpikesLastMinute = PreloadSpikesLastMinute;

        SampleProcess();

        var sb = new StringBuilder(2048);
        sb.Append('{');
        Str(sb, "ts", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv));
        Num(sb, "session_seconds", (PerfData.Now - _sessionStartTs) / (double)Stopwatch.Frequency);
        Bool(sb, "in_world", !Main.gameMenu);
        Num(sb, "fps_draw", PerfData.SmoothedFps);
        Num(sb, "fps_draw_ema", PerfData.DrawHz);
        Num(sb, "fps_game", PerfData.GameFps);
        Num(sb, "ups", PerfData.UpdateHz);
        Num(sb, "frame_ms", PerfData.FrameMs);
        Num(sb, "frame_avg_ms", avg);
        Num(sb, "frame_p95_ms", p95);
        Num(sb, "frame_max_ms", max);
        Num(sb, "cpu_update_ms", PerfData.UpdateMs);
        Num(sb, "cpu_draw_ms", PerfData.DrawMs);
        Num(sb, "cursor_ms", PerfData.CursorMs);
        Str(sb, "cursor_state", SmoothCursorSystem.Status);
        Num(sb, "unacc_ms", PerfData.UnaccountedMs);
        Num(sb, "light_ms", PerfData.LightingMs);
        Num(sb, "tiles_walls_ms", PerfData.TilesMs + PerfData.WallsMs);
        Num(sb, "background_ms", PerfData.BackgroundMs);
        Num(sb, "dust_ms", PerfData.DustMs);
        Num(sb, "projectiles_ms", PerfData.ProjectilesMs);
        Num(sb, "npc_ms", PerfData.NpcsMs);
        Num(sb, "items_ms", PerfData.ItemsMs);
        Num(sb, "upd_players_ms", PerfData.UpdPlayersMs);
        Num(sb, "upd_npcs_ms", PerfData.UpdNpcsMs);
        Num(sb, "upd_gores_ms", PerfData.UpdGoresMs);
        Num(sb, "upd_projectiles_ms", PerfData.UpdProjectilesMs);
        Num(sb, "upd_items_ms", PerfData.UpdItemsMs);
        Num(sb, "upd_dust_ms", PerfData.UpdDustMs);
        Num(sb, "upd_time_ms", PerfData.UpdTimeMs);
        Num(sb, "upd_world_ms", PerfData.UpdWorldMs);
        Num(sb, "upd_invasions_ms", PerfData.UpdInvasionsMs);
        Num(sb, "gore_par_ms", PerfData.GoreParallelMs);
        Num(sb, "gore_par_n", PerfData.GoreParallelCount);
        Num(sb, "rain_par_ms", PerfData.RainParallelMs);
        Num(sb, "rain_par_n", PerfData.RainParallelCount);
        Num(sb, "gore_par_total", PerfData.GoreParallelTotal);
        Num(sb, "rain_par_total", PerfData.RainParallelTotal);
        Str(sb, "parallel_state", ParallelState());
        Num(sb, "gc0_per_s", PerfData.Gc0PerSecond);
        Num(sb, "gc2_per_s", PerfData.Gc2PerSecond);
        Num(sb, "gc_pause_ms_per_s", PerfData.GcPauseMsPerSecond);
        Num(sb, "alloc_mb_per_s", PerfData.AllocMbPerSecond);
        Num(sb, "process_cpu_pct", ProcessCpuPct);
        Num(sb, "threads", ThreadCount);
        Num(sb, "section_ms", PerfData.SectionMs);
        Num(sb, "section_n", PerfData.SectionCount);
        Num(sb, "preload_pct", WorldPreloader.Percent);
        Num(sb, "preload_budget_ms", WorldPreloader.EffectiveBudgetMs);
        Num(sb, "preload_ms", WorldPreloader.LastUpdateMs);
        Bool(sb, "preload_active", WorldPreloader.Active);
        Num(sb, "dust", PerfData.DustCount);
        Num(sb, "projectiles", PerfData.ProjectileCount);
        Num(sb, "npcs", PerfData.NpcCount);
        Num(sb, "items", PerfData.ItemCount);
        Num(sb, "spikes_last_60s", spikesLastMinute);
        Num(sb, "preload_spikes_last_60s", preloadSpikesLastMinute);
        Num(sb, "save_ms", PerfData.SaveMs);
        Num(sb, "save_count", PerfData.SaveCount);
        Num(sb, "autosave_deferred", PerfData.AutosaveDeferrals);
        Num(sb, "dropped_writes", AsyncFileWriter.Dropped);
        Num(sb, "failed_writes", AsyncFileWriter.Failed);
        Str(sb, "gc_latency", System.Runtime.GCSettings.LatencyMode.ToString());
        Str(sb, "world", Main.worldName);
        Str(sb, "bosses", Bosses());
        Str(sb, "dominant", Dominant());

        sb.Append("\"player\":{");
        if (player != null)
        {
            sb.Append("\"name\":\"").Append(Escape(player.name)).Append("\",");
            sb.Append("\"x\":").Append(F(player.Center.X / 16f)).Append(',');
            sb.Append("\"y\":").Append(F(player.Center.Y / 16f)).Append(',');
            sb.Append("\"vx\":").Append(F(player.velocity.X)).Append(',');
            sb.Append("\"vy\":").Append(F(player.velocity.Y)).Append(',');
            sb.Append("\"hp\":").Append(player.statLife.ToString(Inv)).Append(',');
            sb.Append("\"hpmax\":").Append(player.statLifeMax2.ToString(Inv)).Append(',');
            sb.Append("\"dead\":").Append(player.dead ? "true" : "false").Append(',');
            sb.Append("\"biome\":\"").Append(Escape(Biome(player))).Append('"');
        }
        else
        {
            sb.Append("\"name\":\"?\"");
        }
        sb.Append('}');

        sb.Append(",\"last_spikes\":[");
        int start = Math.Max(0, Spikes.Count - SpikeLiveCount);
        for (int i = start; i < Spikes.Count; i++)
        {
            if (i > start)
                sb.Append(',');

            var s = Spikes[i];
            sb.Append('{');
            Str(sb, "cause", s.Cause ?? "other");
            Num(sb, "frame_ms", s.FrameMs);
            Num(sb, "unacc_ms", Math.Max(0.0, s.FrameMs - s.UpdateMs - s.DrawMs));
            Num(sb, "update_ms", s.UpdateMs);
            Num(sb, "draw_ms", s.DrawMs);
            Num(sb, "light_ms", s.LightMs);
            Num(sb, "npcs", s.NpcCount);
            Num(sb, "projectiles", s.ProjectileCount);
            Num(sb, "gc_pause_ms_s", s.GcPause);
            Num(sb, "upd_npc", s.UpdNpcMs);
            Num(sb, "upd_proj", s.UpdProjMs);
            Num(sb, "gore_par_ms", s.GoreParMs);
            Num(sb, "preload_ms", s.PreloadMs);
            Str(sb, "bosses", s.Bosses);
            sb.Append("\"at\":\"").Append(s.At.ToString("yyyy-MM-dd HH:mm:ss", Inv)).Append("\"}");
        }
        sb.Append(']').Append('}');

        AsyncFileWriter.WriteAtomic(Path.Combine(Dir, "live.json"), sb.ToString());
    }

    private static void FlushSpikes()
    {
        if (SpikeCsv.Count == 0)
            return;

        string path = Path.Combine(Dir, "spikes.csv");
        var sb = new StringBuilder(SpikeCsv.Count * 160);
        for (int i = 0; i < SpikeCsv.Count; i++)
            sb.AppendLine(SpikeCsv[i]);

        AsyncFileWriter.Append(path, sb.ToString(), SpikeHeader + Environment.NewLine);
        SpikeCsv.Clear();
    }

    private static void AppendTimeline()
    {
        string path = Path.Combine(Dir, "timeline.csv");
        var sb = new StringBuilder(512);

        double avg = AvgWindow();
        double p95 = PercentileWindow(0.95);
        double max = MaxWindow();
        var player = Main.LocalPlayer;

        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv)).Append(',');
        sb.Append(F(PerfData.SmoothedFps)).Append(',');
        sb.Append(PerfData.GameFps.ToString(Inv)).Append(',');
        sb.Append(F(PerfData.UpdateHz)).Append(',');
        sb.Append(F(avg)).Append(',');
        sb.Append(F(p95)).Append(',');
        sb.Append(F(max)).Append(',');
        sb.Append(F(PerfData.UpdateMs)).Append(',');
        sb.Append(F(PerfData.DrawMs)).Append(',');
        sb.Append(F(PerfData.LightingMs)).Append(',');
        sb.Append(F(PerfData.TilesMs + PerfData.WallsMs)).Append(',');
        sb.Append(F(PerfData.BackgroundMs)).Append(',');
        sb.Append(F(PerfData.DustMs)).Append(',');
        sb.Append(F(PerfData.ProjectilesMs)).Append(',');
        sb.Append(F(PerfData.NpcsMs)).Append(',');
        sb.Append(F(PerfData.ItemsMs)).Append(',');
        sb.Append(F(PerfData.Gc0PerSecond)).Append(',');
        sb.Append(F(PerfData.AllocMbPerSecond)).Append(',');
        sb.Append(PerfData.DustCount.ToString(Inv)).Append(',');
        sb.Append(PerfData.ProjectileCount.ToString(Inv)).Append(',');
        sb.Append(PerfData.NpcCount.ToString(Inv)).Append(',');
        sb.Append(PerfData.ItemCount.ToString(Inv)).Append(',');
        sb.Append(Csv(Bosses())).Append(',');
        sb.Append(Csv(player != null ? Biome(player) : "?")).Append(',');
        sb.Append(F(player != null ? player.Center.X / 16f : 0f)).Append(',');
        sb.Append(F(player != null ? player.Center.Y / 16f : 0f)).Append(',');
        sb.Append(F(PerfData.Gc2PerSecond)).Append(',');
        sb.Append(F(PerfData.GcPauseMsPerSecond)).Append(',');
        sb.Append(F(PerfData.UpdPlayersMs)).Append(',');
        sb.Append(F(PerfData.UpdNpcsMs)).Append(',');
        sb.Append(F(PerfData.UpdGoresMs)).Append(',');
        sb.Append(F(PerfData.UpdProjectilesMs)).Append(',');
        sb.Append(F(PerfData.UpdItemsMs)).Append(',');
        sb.Append(F(PerfData.UpdDustMs)).Append(',');
        sb.Append(F(PerfData.UpdTimeMs)).Append(',');
        sb.Append(F(PerfData.UpdWorldMs)).Append(',');
        sb.Append(F(PerfData.UpdInvasionsMs)).Append(',');
        sb.Append(F(PerfData.GoreParallelMs)).Append(',');
        sb.Append(PerfData.GoreParallelCount.ToString(Inv)).Append(',');
        sb.Append(F(PerfData.RainParallelMs)).Append(',');
        sb.Append(PerfData.RainParallelCount.ToString(Inv)).Append(',');
        sb.Append(F(ProcessCpuPct)).Append(',');
        sb.Append(ThreadCount.ToString(Inv)).Append(',');
        sb.Append(F(PerfData.SectionMs)).Append(',');
        sb.Append(PerfData.SectionCount.ToString(Inv)).Append(',');
        sb.Append(F(WorldPreloader.Percent)).Append(',');
        sb.Append(F(PerfData.CursorMs)).Append(',');
        sb.Append(F(WorldPreloader.LastUpdateMs));

        AsyncFileWriter.Append(path, sb.ToString() + Environment.NewLine, TimelineHeader + Environment.NewLine);
    }

    private static string ToCsv(Spike s) =>
        string.Join(",", new[]
        {
            s.At.ToString("yyyy-MM-dd HH:mm:ss", Inv),
            F(s.FrameMs),
            F(s.UpdateMs),
            F(s.DrawMs),
            F(s.LightMs),
            F(s.TilesWallsMs),
            F(s.BackgroundMs),
            F(s.DustMs),
            F(s.ProjectilesMs),
            F(s.NpcMs),
            F(s.ItemsMs),
            F(s.Fps),
            F(s.Gc0),
            F(s.AllocMb),
            s.DustCount.ToString(Inv),
            s.ProjectileCount.ToString(Inv),
            s.NpcCount.ToString(Inv),
            s.ItemCount.ToString(Inv),
            Csv(s.Bosses),
            Csv(s.Biome),
            F(s.X),
            F(s.Y),
            F(s.Vx),
            F(s.Vy),
            s.Hp.ToString(Inv),
            s.Dead ? "1" : "0",
            F(s.Gc2),
            F(s.GcPause),
            F(s.UpdNpcMs),
            F(s.UpdProjMs),
            F(s.UpdGoreMs),
            F(s.UpdDustMs),
            F(s.GoreParMs),
            s.GoreParN.ToString(Inv),
            F(s.RainParMs),
            s.RainParN.ToString(Inv),
            F(s.PreloadMs),
            s.Cause ?? "other",
            F(Math.Max(0.0, s.FrameMs - s.UpdateMs - s.DrawMs))
        });

    // Причина спайка: кто был последней миллисекундой, вытолкнувшей кадр
    // за бюджет.
    private static string ClassifySpike(double frameMs, double updateCpu, double drawCpu)
    {
        double preload = PerfData.PreloadMsThisFrame;
        double update = Math.Max(0.0, updateCpu - preload);
        double draw = drawCpu;
        double wait = Math.Max(0.0, frameMs - updateCpu - drawCpu);

        if (PerfData.SaveThisFrameMs >= 1.0 && PerfData.SaveThisFrameMs >= update * 0.5)
            return "save";

        if (PerfData.SectionMs >= 1.0 && PerfData.SectionMs >= update * 0.5)
            return "section";

        if (PerfData.GcPauseThisFrameMs >= frameMs * 0.35)
            return "gc";

        int rate = Main.frameRate > 0 ? Main.frameRate : 60;
        double targetMs = 1000.0 / Math.Clamp(rate, 30, 240);

        double cpu = updateCpu + drawCpu;
        double cpuWithoutPreload = cpu - preload;

        // Срез предзагрузки был последней миллисекундой, вытолкнувшей кадр
        // за бюджет: без него update+draw уложились бы.
        if (preload >= 1.0 && cpu > targetMs - 1.0 && cpuWithoutPreload <= targetMs - 1.0)
            return "preload";

        // Кадр не упирался в процессор — ждали present/GPU.
        if (wait >= frameMs * 0.4 && wait >= update && wait >= draw)
            return "gpu";

        if (update >= draw * 1.25)
            return "update";

        if (draw >= update * 1.25)
            return "draw";

        return "other";
    }

    public static int SpikesLastMinute => CountSpikesLastMinute(false);
    public static int PreloadSpikesLastMinute => CountSpikesLastMinute(true);

    private static int CountSpikesLastMinute(bool preload)
    {
        long limit = PerfData.Now - (long)(Stopwatch.Frequency * 60.0);
        int count = 0;

        for (int i = SpikeTimes.Count - 1; i >= 0; i--)
        {
            if (SpikeTimes[i] < limit)
                break;

            bool isPreload = i < SpikeCauses.Count && SpikeCauses[i] == "preload";
            if (isPreload == preload)
                count++;
        }

        return count;
    }

    private static double AvgWindow()
    {
        if (_windowCount == 0)
            return 0.0;

        double sum = 0;
        for (int i = 0; i < _windowCount; i++)
            sum += Window[i];
        return sum / _windowCount;
    }

    private static double MaxWindow()
    {
        double max = 0;
        for (int i = 0; i < _windowCount; i++)
        {
            if (Window[i] > max)
                max = Window[i];
        }
        return max;
    }

    private static double PercentileWindow(double p)
    {
        if (_windowCount == 0)
            return 0.0;

        var copy = WindowCopy;
        Array.Copy(Window, copy, _windowCount);
        Array.Sort(copy, 0, _windowCount);
        int index = Math.Clamp((int)(_windowCount * p), 0, _windowCount - 1);
        return copy[index];
    }

    private static string ParallelState()
    {
        if (GoreParallel.Failed || RainParallel.Failed)
            return "failed";

        bool gore = GoreParallel.Enabled;
        bool rain = RainParallel.Enabled;

        if (gore && rain)
            return "gore+rain";
        if (gore)
            return "gore";
        if (rain)
            return "rain";
        return "off";
    }

    private static string Dominant()
    {
        if (PerfData.LightingMs > PerfData.DrawMs * 0.5)
            return "lighting";
        if (PerfData.UpdateMs > PerfData.DrawMs * 1.4)
            return "update:" + DominantUpdateSection();
        if (PerfData.DrawMs > PerfData.UpdateMs * 1.4)
            return "draw";
        return "balanced";
    }

    private static string DominantUpdateSection()
    {
        string name = "npc";
        double best = PerfData.UpdNpcsMs;

        if (PerfData.UpdProjectilesMs > best)
        {
            best = PerfData.UpdProjectilesMs;
            name = "proj";
        }
        if (PerfData.UpdDustMs > best)
        {
            best = PerfData.UpdDustMs;
            name = "dust";
        }
        if (PerfData.UpdWorldMs > best)
        {
            best = PerfData.UpdWorldMs;
            name = "world";
        }
        if (PerfData.UpdPlayersMs > best)
        {
            best = PerfData.UpdPlayersMs;
            name = "player";
        }
        if (PerfData.UpdGoresMs > best)
        {
            best = PerfData.UpdGoresMs;
            name = "gore";
        }
        return name;
    }

    private static string Bosses()
    {
        if (Main.npc == null)
            return "";

        var names = new List<string>();
        for (int i = 0; i < Main.npc.Length; i++)
        {
            var npc = Main.npc[i];
            if (!npc.active || !npc.boss)
                continue;

            // Сегменты многочастного босса считаем один раз.
            if (npc.realLife >= 0 && npc.realLife != npc.whoAmI)
                continue;

            names.Add(npc.FullName);
            if (names.Count >= 6)
                break;
        }

        return string.Join("|", names);
    }

    private static string Biome(Player player)
    {
        var biomes = new List<string>(3);

        if (player.ZoneDungeon)
            biomes.Add("Dungeon");
        else if (player.ZoneUnderworldHeight)
            biomes.Add("Underworld");

        if (player.ZoneCorrupt)
            biomes.Add("Corrupt");
        if (player.ZoneCrimson)
            biomes.Add("Crimson");
        if (player.ZoneHallow)
            biomes.Add("Hallow");
        if (player.ZoneJungle)
            biomes.Add("Jungle");
        if (player.ZoneSnow)
            biomes.Add("Snow");
        if (player.ZoneDesert)
            biomes.Add("Desert");
        if (player.ZoneBeach)
            biomes.Add("Beach");
        if (player.ZoneGlowshroom)
            biomes.Add("Glowshroom");
        if (player.ZoneMeteor)
            biomes.Add("Meteor");
        if (player.ZoneShimmer)
            biomes.Add("Shimmer");

        if (biomes.Count == 0)
            biomes.Add(player.ZonePurity ? "Surface" : "Purity");

        if (player.ZoneRockLayerHeight)
            biomes.Add("Caverns");
        else if (player.ZoneDirtLayerHeight)
            biomes.Add("Underground");
        else if (player.ZoneSkyHeight)
            biomes.Add("Sky");

        return string.Join("|", biomes);
    }

    private static void Str(StringBuilder sb, string key, string value) =>
        sb.Append('"').Append(key).Append("\":\"").Append(Escape(value ?? "")).Append("\",");

    private static void Num(StringBuilder sb, string key, double value) =>
        sb.Append('"').Append(key).Append("\":").Append(F(value)).Append(',');

    private static void Bool(StringBuilder sb, string key, bool value) =>
        sb.Append('"').Append(key).Append("\":").Append(value ? "true" : "false").Append(',');

    private static string F(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return "0";
        return value.ToString("0.###", Inv);
    }

    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                default:
                    sb.Append(c < ' ' ? ' ' : c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            return "\"" + value.Replace("\"", "\"\"") + "\"";

        return value;
    }
}
