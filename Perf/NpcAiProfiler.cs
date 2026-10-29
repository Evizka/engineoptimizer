using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Terraria;

namespace EngineOptimizer.Perf;

// v0.8.11: кто из NPC ест время update — тайминг NPC.AI по типам.
// Отчёт v0.8.9 показал: при спавне городских NPC upd_npc подскакивал до
// 10-13 мс (макс 37.6), но кто именно виноват — не видно. Хук дешёвый:
// один Stopwatch на вызов (~40 вызовов/кадр), накопление в массивы без
// аллокаций; снимок топ-3 — раз в кадр, итоги сессии — при выходе.
public static class NpcAiProfiler
{
    private const int MaxTypes = 2048; // NPCID.Count ~ 700, моды добавляют
    private static readonly double[] _frameMs = new double[MaxTypes];
    private static readonly int[] _frameCalls = new int[MaxTypes];
    private static readonly double[] _sessionMs = new double[MaxTypes];
    private static readonly long[] _sessionCalls = new long[MaxTypes];
    private static bool _hooked;

    public static double FrameMs { get; private set; }
    public static string FrameTop { get; private set; } = "";

    public static void Hook()
    {
        if (_hooked)
            return;
        _hooked = true;
        On_NPC.AI += OnAi;
    }

    public static void Unhook()
    {
        if (!_hooked)
            return;
        _hooked = false;
        On_NPC.AI -= OnAi;
    }

    private static void OnAi(On_NPC.orig_AI orig, NPC self)
    {
        if (self == null || !self.active)
        {
            orig(self);
            return;
        }

        long start = PerfData.Now;
        orig(self);
        double ms = PerfData.MsSince(start);

        int t = self.type;
        if ((uint)t < (uint)MaxTypes)
        {
            _frameMs[t] += ms;
            _frameCalls[t]++;
            _sessionMs[t] += ms;
            _sessionCalls[t]++;
        }
    }

    // PreUpdateNPCs: новый кадр.
    public static void BeginFrame()
    {
        if (!_hooked)
            return;
        Array.Clear(_frameMs, 0, _frameMs.Length);
        Array.Clear(_frameCalls, 0, _frameCalls.Length);
    }

    // PostUpdateNPCs: снимок топ-3 за кадр (для live.json).
    public static void EndFrame()
    {
        if (!_hooked)
        {
            FrameMs = 0;
            FrameTop = "";
            return;
        }

        double total = 0;
        int t1 = -1, t2 = -1, t3 = -1;
        double m1 = 0, m2 = 0, m3 = 0;
        for (int i = 0; i < _frameMs.Length; i++)
        {
            double m = _frameMs[i];
            if (m <= 0)
                continue;
            total += m;
            if (m > m1)
            {
                t3 = t2; m3 = m2; t2 = t1; m2 = m1; t1 = i; m1 = m;
            }
            else if (m > m2)
            {
                t3 = t2; m3 = m2; t2 = i; m2 = m;
            }
            else if (m > m3)
            {
                t3 = i; m3 = m;
            }
        }

        FrameMs = total;
        if (t1 < 0)
        {
            FrameTop = "";
            return;
        }

        var sb = new StringBuilder(64);
        Append(sb, t1, m1, _frameCalls[t1]);
        if (t2 >= 0)
        {
            sb.Append("; ");
            Append(sb, t2, m2, _frameCalls[t2]);
        }
        if (t3 >= 0)
        {
            sb.Append("; ");
            Append(sb, t3, m3, _frameCalls[t3]);
        }
        FrameTop = sb.ToString();
    }

    public static void ResetSession()
    {
        Array.Clear(_sessionMs, 0, _sessionMs.Length);
        Array.Clear(_sessionCalls, 0, _sessionCalls.Length);
        FrameMs = 0;
        FrameTop = "";
    }

    // Итог сессии: топ типов по суммарному времени NPC.AI.
    public static List<string> SessionTop(int maxLines)
    {
        var result = new List<string>(maxLines);
        if (!_hooked)
            return result;

        int[] top = new int[maxLines];
        double[] topMs = new double[maxLines];
        for (int i = 0; i < _sessionMs.Length; i++)
        {
            double m = _sessionMs[i];
            if (m <= 0)
                continue;
            for (int slot = 0; slot < maxLines; slot++)
            {
                if (m > topMs[slot])
                {
                    for (int shift = maxLines - 1; shift > slot; shift--)
                    {
                        top[shift] = top[shift - 1];
                        topMs[shift] = topMs[shift - 1];
                    }
                    top[slot] = i;
                    topMs[slot] = m;
                    break;
                }
            }
        }

        var sb = new StringBuilder(64);
        for (int slot = 0; slot < maxLines; slot++)
        {
            int type = top[slot];
            if (type == 0 && topMs[slot] <= 0)
                continue;

            sb.Clear();
            sb.Append("  type ").Append(type.ToString(CultureInfo.InvariantCulture));
            string name = NpcName(type);
            if (!string.IsNullOrEmpty(name))
                sb.Append(" '").Append(name).Append("'");
            sb.Append(": ").Append(topMs[slot].ToString("0", CultureInfo.InvariantCulture))
                .Append(" ms total, avg ")
                .Append((_sessionCalls[type] > 0 ? topMs[slot] / _sessionCalls[type] : 0).ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" ms x").Append(_sessionCalls[type].ToString(CultureInfo.InvariantCulture));
            result.Add(sb.ToString());
        }

        return result;
    }

    // Имя типа: ищем живой экземпляр в Main.npc (дёшево, типы из топа
    // почти всегда на месте); если нет — без имени.
    private static string NpcName(int type)
    {
        try
        {
            var npcs = Main.npc;
            for (int i = 0; i < npcs.Length; i++)
            {
                var n = npcs[i];
                if (n != null && n.active && n.type == type)
                    return n.TypeName;
            }
        }
        catch
        {
        }
        return null;
    }
}
