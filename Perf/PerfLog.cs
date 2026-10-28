using System;
using System.Globalization;
using System.IO;
using System.Text;
using Terraria;

namespace EngineOptimizer.Perf;

// Итоговый CSV-отчёт при выходе из мира (profile-*.csv): avg/p50/p95/p99/max
// по кадру/update/draw/освещению плюс сводка GC и сущностей.
public static class PerfLog
{
    private const int MaxSamples = 120000;

    private static readonly float[] Frames = new float[MaxSamples];
    private static readonly float[] Updates = new float[MaxSamples];
    private static readonly float[] Draws = new float[MaxSamples];
    private static readonly float[] Lights = new float[MaxSamples];
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static int _count;

    public static void Reset() => _count = 0;

    public static void Record(double frame, double update, double draw, double light)
    {
        if (_count >= MaxSamples)
            return;

        Frames[_count] = (float)frame;
        Updates[_count] = (float)update;
        Draws[_count] = (float)draw;
        Lights[_count] = (float)light;
        _count++;
    }

    public static string Save()
    {
        int count = _count;
        if (count < 120)
            return null;

        string dir = Path.Combine(Main.SavePath, "EngineOptimizer");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"profile-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        var sb = new StringBuilder(1024);
        sb.AppendLine("metric,value");
        Append(sb, "frames", count.ToString(CultureInfo.InvariantCulture));
        Append(sb, "frame_avg_ms", Avg(Frames, count).ToString("0.00", CultureInfo.InvariantCulture));
        Append(sb, "frame_p50_ms", Percentile(Frames, count, 0.5f).ToString("0.00", CultureInfo.InvariantCulture));
        Append(sb, "frame_p95_ms", Percentile(Frames, count, 0.95f).ToString("0.00", CultureInfo.InvariantCulture));
        Append(sb, "frame_p99_ms", Percentile(Frames, count, 0.99f).ToString("0.00", CultureInfo.InvariantCulture));
        Append(sb, "frame_max_ms", Max(Frames, count).ToString("0.00", CultureInfo.InvariantCulture));
        Append(sb, "fps_avg", (1000.0 / Math.Max(0.01, Avg(Frames, count))).ToString("0.0", CultureInfo.InvariantCulture));
        Append(sb, "update_avg_ms", Avg(Updates, count).ToString("0.00", CultureInfo.InvariantCulture));
        Append(sb, "update_p95_ms", Percentile(Updates, count, 0.95f).ToString("0.00", CultureInfo.InvariantCulture));
        Append(sb, "draw_avg_ms", Avg(Draws, count).ToString("0.00", CultureInfo.InvariantCulture));
        Append(sb, "draw_p95_ms", Percentile(Draws, count, 0.95f).ToString("0.00", CultureInfo.InvariantCulture));
        Append(sb, "lighting_avg_ms", Avg(Lights, count).ToString("0.00", CultureInfo.InvariantCulture));
        Append(sb, "lighting_p95_ms", Percentile(Lights, count, 0.95f).ToString("0.00", CultureInfo.InvariantCulture));
        Append(sb, "gc0_per_sec", PerfData.Gc0PerSecond.ToString("0.00", CultureInfo.InvariantCulture));
        Append(sb, "gc2_per_sec", PerfData.Gc2PerSecond.ToString("0.00", CultureInfo.InvariantCulture));
        Append(sb, "gc_pause_ms_per_sec", PerfData.GcPauseMsPerSecond.ToString("0.00", CultureInfo.InvariantCulture));
        Append(sb, "alloc_mb_per_sec", PerfData.AllocMbPerSecond.ToString("0.00", CultureInfo.InvariantCulture));
        Append(sb, "dust_active", PerfData.DustCount.ToString(CultureInfo.InvariantCulture));
        Append(sb, "projectiles_active", PerfData.ProjectileCount.ToString(CultureInfo.InvariantCulture));
        Append(sb, "npcs_active", PerfData.NpcCount.ToString(CultureInfo.InvariantCulture));
        Append(sb, "items_active", PerfData.ItemCount.ToString(CultureInfo.InvariantCulture));

        File.WriteAllText(path, sb.ToString(), Utf8);
        return path;
    }

    private static void Append(StringBuilder sb, string key, string value) =>
        sb.Append(key).Append(',').AppendLine(value);

    private static double Avg(float[] values, int count)
    {
        double sum = 0;
        for (int i = 0; i < count; i++)
            sum += values[i];
        return sum / count;
    }

    private static double Max(float[] values, int count)
    {
        double max = 0;
        for (int i = 0; i < count; i++)
        {
            if (values[i] > max)
                max = values[i];
        }
        return max;
    }

    private static double Percentile(float[] values, int count, float p)
    {
        var copy = new float[count];
        Array.Copy(values, copy, count);
        Array.Sort(copy);
        int index = Math.Clamp((int)(copy.Length * p), 0, copy.Length - 1);
        return copy[index];
    }
}
