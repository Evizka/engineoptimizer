using System;
using System.Collections.Generic;
using System.Threading;
using Terraria;
using Terraria.ID;
using Terraria.Utilities;
using ReLogic.Threading;

namespace EngineOptimizer.Perf;

// Фасад UnifiedRandom с генератором на каждый поток: Main.rand подменяется им
// на время параллельной фазы (gore/rain), чтобы воркеры не дрались за общий
// генератор случайных чисел.
public sealed class ThreadLocalRandom : UnifiedRandom
{
    public static readonly ThreadLocalRandom Instance = new ThreadLocalRandom();

    [ThreadStatic]
    private static UnifiedRandom _local;
    private static int _seed;

    private static UnifiedRandom Local
    {
        get
        {
            if (_local == null)
            {
                _local = new UnifiedRandom(
                    Environment.TickCount * 31
                    + Interlocked.Increment(ref _seed) * 7919
                    + Thread.CurrentThread.ManagedThreadId);
            }
            return _local;
        }
    }

    public override int Next() => Local.Next();
    public override int Next(int maxValue) => Local.Next(maxValue);
    public override int Next(int minValue, int maxValue) => Local.Next(minValue, maxValue);
    public override double NextDouble() => Local.NextDouble();
    public override void NextBytes(byte[] buffer) => Local.NextBytes(buffer);
    protected override double Sample() => Local.NextDouble();
}

// Общий признак «сейчас идёт параллельная фаза» и лок для эффектов.
// В обычном коде чтение Busy — один volatile-read; Dust.NewDust и
// Lighting.AddLight берут лок только пока фаза активна.
public static class ParallelEffects
{
    public static readonly object Lock = new object();
    public static volatile bool Busy;
}

// Параллельное обновление ванильного gore на пуле потоков.
// Модовый gore (Gore.ModGore), капли жидкости, особые типы (облака, туман,
// искры), типы 411-430 и 1218 (спавнят dust), тип 1208 (спавнит gore и пишет
// тайлы) и всё, что светится, остаётся на главном потоке.
public static class GoreParallel
{
    public const int MinCount = 128;

    public static Action<string> Log;
    public static bool Failed;
    public static double LastMs;
    public static int LastCount;

    private static readonly HashSet<Gore> Safe = new HashSet<Gore>(1024);
    private static readonly bool[] SafeFlags = new bool[601];
    private static bool _skipVanilla;
    private static bool _parallelPhase;
    private static volatile bool _running;
    private static int _phaseId;
    private static Exception _error;
    private static int _errors;

    public static bool Enabled => !Failed && RuntimeConfig.ParallelGore; // v1.1: пресет

    public static void Reset()
    {
        Failed = false;
        _errors = 0;
        _error = null;
    }

    public static void End() => _skipVanilla = false;

    // Вызывается из хука Gore.Update: ванильный цикл пропускает обновление
    // gore, который уже обновили воркеры.
    public static bool ShouldSkip(Gore gore) =>
        _skipVanilla && !_parallelPhase && Safe.Contains(gore);

    public static void Run()
    {
        _skipVanilla = false;
        LastMs = 0;
        LastCount = 0;
        PerfData.SetGoreParallel(0.0, 0);

        if (!Enabled || Main.dedServ || Main.gameMenu || Main.gore == null)
            return;

        var gores = Main.gore;
        int limit = Math.Min(gores.Length, 600);

        Safe.Clear();
        Array.Clear(SafeFlags, 0, SafeFlags.Length);

        for (int i = 0; i < limit; i++)
        {
            var gore = gores[i];
            if (gore == null || !gore.active || gore.ModGore != null)
                continue;

            int type = gore.type;
            if (type >= 0 && type < GoreID.Sets.LiquidDroplet.Length && GoreID.Sets.LiquidDroplet[type])
                continue;
            if (type >= 0 && type < GoreID.Sets.SpecialAI.Length && GoreID.Sets.SpecialAI[type] != 0)
                continue;
            if (type == 1208 || type == 1218 || (type >= 411 && type <= 430))
                continue;
            if (gore.light > 0f)
                continue;

            Safe.Add(gore);
            SafeFlags[i] = true;
        }

        // Меньше 128 «безопасных» gore — накладные расходы пула больше выигрыша.
        if (Safe.Count < MinCount)
            return;

        long start = PerfData.Now;
        var savedRand = Main.rand;
        int phase = ++_phaseId;
        _error = null;
        _parallelPhase = true;
        _running = true;
        ParallelEffects.Busy = true;
        Main.rand = ThreadLocalRandom.Instance;

        try
        {
            FastParallel.For(0, limit, UpdateRange, phase);
        }
        catch (Exception e)
        {
            _error = e;
            _errors = int.MaxValue;
        }
        finally
        {
            _running = false;
            Main.rand = savedRand;
            ParallelEffects.Busy = false;
            _parallelPhase = false;

            if (_error != null)
            {
                // Три ошибки подряд (или watchdog FastParallel) — выключаем до
                // следующей загрузки мира.
                if (_errors >= 3)
                    Failed = true;
                Log?.Invoke("Parallel gore update failed: " + _error?.ToString());
            }
            else
            {
                _errors = 0;
            }

            _skipVanilla = true;
            LastMs = PerfData.MsSince(start);
            LastCount = Safe.Count;
            PerfData.SetGoreParallel(LastMs, LastCount);
        }
    }

    private static void UpdateRange(int fromInclusive, int toExclusive, object context)
    {
        int phase = (int)context;

        if (!_running || Volatile.Read(ref _phaseId) != phase)
            return;

        var gores = Main.gore;

        for (int i = fromInclusive; i < toExclusive; i++)
        {
            if (!_running || Volatile.Read(ref _phaseId) != phase)
                return;

            if (!SafeFlags[i])
                continue;

            var gore = gores[i];
            if (gore == null || !gore.active)
                continue;

            try
            {
                gore.Update();
            }
            catch (Exception e)
            {
                _error = e;
                _errors++;
                // Сломанный gore заменяем свежим, чтобы массив не «взрывался».
                gores[i] = new Gore();
            }
        }
    }
}

// Параллельное обновление капель дождя до отрисовки (эксперимент, по
// умолчанию выключено: HighFPSSupport уже хукает Rain.Update).
public static class RainParallel
{
    public const int MinCount = 400;

    public static Action<string> Log;
    public static bool Failed;
    public static double LastMs;
    public static int LastCount;

    private static bool _skipVanilla;
    private static bool _parallelPhase;
    private static volatile bool _running;
    private static int _phaseId;
    private static Exception _error;

    public static bool Enabled => !Failed && RuntimeConfig.ParallelRain; // v1.1: пресет

    public static void Reset()
    {
        Failed = false;
        _error = null;
    }

    public static void End() => _skipVanilla = false;

    public static bool ShouldSkip(Rain rain) =>
        _skipVanilla && !_parallelPhase;

    // Возвращает true, если капли уже обновлены параллельно и ванильный
    // DrawRain должен пропустить обновление.
    public static bool Begin()
    {
        _skipVanilla = false;
        LastMs = 0;
        LastCount = 0;
        PerfData.SetRainParallel(0.0, 0);

        if (!Enabled || Main.dedServ || Main.gameMenu || Main.rain == null || Main.gamePaused)
            return false;

        // Неактивное окно в одиночной игре: обновлять нечего (и не нужно),
        // в мультиплеере капли продолжает вести сервер.
        if (Main.instance != null && !Main.instance.IsActive && Main.netMode != 1)
            return false;

        int limit = Math.Min(Main.rain.Length, Main.maxRain);

        int active = 0;
        for (int i = 0; i < limit; i++)
        {
            var drop = Main.rain[i];
            if (drop != null && drop.active)
                active++;
        }

        // Меньше 400 активных капель — не Worth it.
        if (active < MinCount)
            return false;

        long start = PerfData.Now;
        var savedRand = Main.rand;
        int phase = ++_phaseId;
        _error = null;
        _parallelPhase = true;
        _running = true;
        ParallelEffects.Busy = true;
        Main.rand = ThreadLocalRandom.Instance;

        try
        {
            FastParallel.For(0, limit, UpdateRange, phase);
        }
        catch (Exception e)
        {
            _error = e;
        }
        finally
        {
            _running = false;
            Main.rand = savedRand;
            ParallelEffects.Busy = false;
            _parallelPhase = false;

            if (_error != null)
            {
                // Любая ошибка выключает rain-режим до перезагрузки мира.
                Failed = true;
                Log?.Invoke("Parallel rain disabled after an error: " + _error?.ToString());
            }

            _skipVanilla = true;
            LastMs = PerfData.MsSince(start);
            LastCount = active;
            PerfData.SetRainParallel(LastMs, LastCount);
        }

        return true;
    }

    private static void UpdateRange(int fromInclusive, int toExclusive, object context)
    {
        int phase = (int)context;

        if (!_running || Volatile.Read(ref _phaseId) != phase)
            return;

        var rain = Main.rain;

        for (int i = fromInclusive; i < toExclusive; i++)
        {
            if (!_running || Volatile.Read(ref _phaseId) != phase)
                return;

            var drop = rain[i];
            if (drop == null || !drop.active)
                continue;

            try
            {
                drop.Update();
            }
            catch (Exception e)
            {
                _error = e;
                Failed = true;
            }
        }
    }
}
