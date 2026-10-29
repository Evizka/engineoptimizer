using System;
using System.Collections.Generic;
using System.Diagnostics;
using Terraria;

namespace EngineOptimizer.Perf;

// Предзагрузка фреймов всех секций мира сразу после входа (одиночная игра
// и клиент мультиплеера; на сервере и у хоста netMode 2 не работает):
// убирает рывки при ходьбе по новым местам. Работа идёт не секциями,
// а кусками тайлов с проверкой дедлайна после каждого тайла — один Reframe
// дерева/самоцвета может стоить миллисекунды.
// v0.8.10: в мультиплеере секции подгружаются с сервера постепенно —
// недогруженные перепланируются на повторный проход (раз в 15 с), пока
// не окажутся у клиента.
public static class WorldPreloader
{
    public static Action<string> Log;

    private struct Section
    {
        public short X;
        public short Y;
        public int DistanceSq;
    }

    private static readonly List<Section> _queue = new List<Section>(1024);
    private static int _queueIndex;
    private static long _startedAt;
    private static double _wallSeconds;
    private static double _baselineMs;
    private static bool _sectionActive;
    private static int _curX;
    private static int _curY;
    private static int _tileX;
    private static int _tileY;
    private static int _tileStartY;
    private static int _tileEndX;
    private static int _tileEndY;
    private static int _sectionTileDone;
    private static int _sectionTileTotal;
    private static double _sectionMs;
    private static bool _noMapUpdateSet;
    private static int _skippedNotLoaded;
    private static long _retryAt;
    private const double RetryDelaySeconds = 15.0;
    // v0.8.11: центр очереди (секция игрока на момент входа) и окно
    // ускоренной загрузки после входа/телепорта.
    private static int _centerSectionX;
    private static int _centerSectionY;
    private static long _entryUntil;
    private const double EntryWindowSeconds = 10.0;
    private const double ResortEntryWindowSeconds = 4.0;
    private const int ResortDistanceSq = 8;

    public static bool Active { get; private set; }
    public static bool Failed { get; private set; }
    public static int Done { get; private set; }
    public static int Total { get; private set; }

    public static double Percent
    {
        get
        {
            if (Total <= 0 || (!Active && FramedSections > 0))
                return 100.0;

            double done = Done;
            if (_sectionActive && _sectionTileTotal > 0)
                done += Math.Min(1.0, (double)_sectionTileDone / _sectionTileTotal);

            return Math.Min(100.0, done * 100.0 / Total);
        }
    }

    public static double EstimatedSecondsLeft
    {
        get
        {
            if (!Active || FramedSections < 4 || AverageSectionMs <= 0.0)
                return 0.0;

            double workSeconds = Math.Max(0, Total - Done - (_sectionActive ? 1 : 0)) * AverageSectionMs / 1000.0;

            double budget = EffectiveBudgetMs > 0.0
                ? EffectiveBudgetMs
                : OptimizerConfig.Instance?.PreloadBudgetMs ?? 5;

            if (budget <= 0.0)
                return 0.0;

            // В кадре реально остаётся лишь малая доля бюджета.
            return workSeconds / (budget * 0.07);
        }
    }

    public static double TotalMs { get; private set; }
    public static double MaxSectionMs { get; private set; }
    public static int FramedSections { get; private set; }

    public static double AverageSectionMs => FramedSections > 0 ? TotalMs / FramedSections : 0.0;

    public static double Seconds
    {
        get
        {
            if (!Active && _wallSeconds > 0.0)
                return _wallSeconds;

            if (_startedAt != 0)
                return (PerfData.Now - _startedAt) / (double)Stopwatch.Frequency;

            return 0.0;
        }
    }

    public static double EffectiveBudgetMs { get; private set; }
    public static double LastUpdateMs { get; private set; }

    static WorldPreloader()
    {
    }

    public static void Reset()
    {
        EndNoMapUpdate();
        Active = false;
        Failed = false;
        _queue.Clear();
        _queueIndex = 0;
        Done = 0;
        Total = 0;
        TotalMs = 0;
        MaxSectionMs = 0;
        FramedSections = 0;
        _startedAt = 0;
        _wallSeconds = 0;
        LastUpdateMs = 0;
        EffectiveBudgetMs = 0;
        _baselineMs = 0;
        _skippedNotLoaded = 0;
        _retryAt = 0;
        _entryUntil = 0;
        ResetSectionCursor();
    }

    public static void Begin()
    {
        Reset();

        // v0.8.10: работаем и в мультиплеере (клиент, netMode 1): предзагрузка
        // чисто локальная (Reframe не шлёт пакетов), а рывки по недофреймленным
        // секциям у клиента те же. Хост (netMode 2) и сервер остаются ванильными.
        if (Main.dedServ || Main.netMode == 2)
            return;

        if (Main.sectionManager == null)
            return;

        int countX = Math.Max(0, Main.maxSectionsX);
        int countY = Math.Max(0, Main.maxSectionsY);
        if (countX <= 0 || countY <= 0)
            return;

        var player = Main.LocalPlayer;
        int playerSectionX = player != null ? (int)(player.Center.X / 16f) / 200 : 0;
        int playerSectionY = player != null ? (int)(player.Center.Y / 16f) / 150 : 0;

        // v0.8.11: запоминаем центр очереди и открываем стартовое окно.
        _centerSectionX = playerSectionX;
        _centerSectionY = playerSectionY;
        _entryUntil = PerfData.Now + (long)(EntryWindowSeconds * Stopwatch.Frequency);

        _queue.Clear();
        _queue.Capacity = Math.Max(_queue.Capacity, countX * countY);

        for (int sx = 0; sx < countX; sx++)
        {
            for (int sy = 0; sy < countY; sy++)
            {
                int dx = sx - playerSectionX;
                int dy = sy - playerSectionY;
                _queue.Add(new Section { X = (short)sx, Y = (short)sy, DistanceSq = dx * dx + dy * dy });
            }
        }

        // Ближние к игроку секции — первыми.
        _queue.Sort((a, b) => a.DistanceSq.CompareTo(b.DistanceSq));

        Total = _queue.Count;
        Active = Total > 0;
        _startedAt = PerfData.Now;

        Log?.Invoke($"World preload queued: {Total} sections, budget {OptimizerConfig.Instance?.PreloadBudgetMs ?? 5} ms/frame");
    }

    // Один срез предзагрузки. budgetMs — верхняя граница (0 = без ограничения,
    // весь мир сразу).
    public static void Update(double budgetMs)
    {
        double previousPreloadMs = LastUpdateMs;
        LastUpdateMs = 0;
        EffectiveBudgetMs = 0;

        // v0.8.10: в мультиплеере недогруженные секции приходят с сервера
        // позже — перепланируем повторный проход, пока всё не зафреймлено.
        if (!Active && _retryAt != 0)
        {
            if (PerfData.Now < _retryAt)
                return;
            _retryAt = 0;
            if (Main.netMode != 2 && !Main.dedServ && !Main.gameMenu)
                Begin();
            return;
        }

        if (!Active)
            return;

        if (Main.gameMenu || Main.sectionManager == null)
        {
            EndNoMapUpdate();
            return;
        }

        if (Main.netMode == 2)
        {
            Reset();
            return;
        }

        // Босс рядом — не тратим кадр на предзагрузку.
        if (BossNearby())
        {
            EndNoMapUpdate();
            return;
        }

        // v0.8.11: игрок ушёл далеко от точки входа (зеркало/телепорт на базу) —
        // очередь была отсортирована вокруг старого центра: пересортируем,
        // иначе молотим секции, где игрока уже нет, а рядом с ним ваниль
        // рефреймит секции сама (93.5 мс спайк в отчёте v0.8.9).
        MaybeResort();

        long start = PerfData.Now;
        double effectiveBudget = budgetMs;

        if (budgetMs > 0.0)
        {
            // Адаптивный губернатор: из бюджета кадра вычитаем уже потраченное
            // update+draw и оставляем 1 мс запаса.
            int rate = Main.frameRate > 0 ? Main.frameRate : 60;
            double target = 1000.0 / Math.Clamp(rate, 30, 240);

            double baseline = PerfData.LastUpdateRawMs - previousPreloadMs + PerfData.LastDrawRawMs;
            if (baseline < 0.0)
                baseline = 0.0;

            if (_baselineMs > 0.0)
                _baselineMs = _baselineMs * 0.85 + baseline * 0.15;
            else
                _baselineMs = baseline;

            // v0.8.11: консервативно — максимум из EMA и последнего кадра.
            // EMA отстаёт на всплесках, и предзагрузка добавляла свои 3 мс
            // к уже тяжёлому кадру (26 «preload»-спайков в отчёте v0.8.9).
            double expected = Math.Max(_baselineMs, baseline);

            // v0.8.11: стартовое окно — первые секунды после входа/телепорта
            // мир и так кашляет (спавн городских NPC, первые текстуры):
            // грузим секции ускоренно (мин 5 мс/кадр), чтобы успеть до того,
            // как игрок дойдёт до незафреймленных мест.
            bool entryWindow = PerfData.Now < _entryUntil;
            double headroom = entryWindow
                ? Math.Max(budgetMs, 5.0)
                : target - expected - 1.0;

            // v0.8.11: предыдущий кадр уже не влез в бюджет — этот срез
            // пропускаем целиком, ничего не подкладываем сверху.
            if (!entryWindow && PerfData.FrameMs > target * 1.5)
                headroom = 0.0;

            if (headroom <= 0.0)
                return;

            effectiveBudget = Math.Min(budgetMs, headroom);
        }

        BeginNoMapUpdate();

        EffectiveBudgetMs = effectiveBudget;
        long workMark = start;

        // Дедлайн в тиках Stopwatch.
        long budget = effectiveBudget > 0.0
            ? (long)(effectiveBudget * Stopwatch.Frequency / 1000.0)
            : long.MaxValue;

        try
        {
            while (true)
            {
                if (!_sectionActive && !StartNextSection())
                {
                    Finish();
                    return;
                }

                while (_tileX < _tileEndX)
                {
                    int j = _tileY;
                    WorldGen.Reframe(_tileX, j, true);
                    _sectionTileDone++;

                    if (++_tileY >= _tileEndY)
                    {
                        _tileY = _tileStartY;
                        _tileX++;
                    }

                    // Дедлайн проверяется после каждого тайла: один Reframe
                    // дерева может стоить миллисекунды.
                    if (PerfData.Now - start >= budget)
                    {
                        AddWork(PerfData.MsSince(workMark));
                        return;
                    }
                }

                Main.sectionManager.SetSectionFramed(_curX, _curY);
                FramedSections++;
                Done++;
                if (_sectionMs > MaxSectionMs)
                    MaxSectionMs = _sectionMs;
                _sectionActive = false;
                _sectionMs = 0.0;

                if (PerfData.Now - start >= budget)
                {
                    AddWork(PerfData.MsSince(workMark));
                    return;
                }
            }
        }
        catch (Exception e)
        {
            Failed = true;
            Active = false;
            EndNoMapUpdate();
            Log?.Invoke("World preload stopped after an error, vanilla framing will continue: " + e?.ToString());
        }
        finally
        {
            LastUpdateMs = PerfData.MsSince(start);
        }
    }

    // v0.8.11: игрок телепортировался/ушёл далеко от центра очереди —
    // пересортируем от него. Уже зафреймленные секции пропускаются
    // быстро (проверка SectionFramed), так что повторный проход дёшев.
    private static void MaybeResort()
    {
        var player = Main.LocalPlayer;
        if (player == null || !player.active)
            return;

        int px = (int)(player.Center.X / 16f) / 200;
        int py = (int)(player.Center.Y / 16f) / 150;
        int dx = px - _centerSectionX;
        int dy = py - _centerSectionY;
        if (dx * dx + dy * dy < ResortDistanceSq)
            return;

        _centerSectionX = px;
        _centerSectionY = py;

        for (int i = 0; i < _queue.Count; i++)
        {
            var s = _queue[i];
            int ex = s.X - px;
            int ey = s.Y - py;
            s.DistanceSq = ex * ex + ey * ey;
            _queue[i] = s;
        }

        _queue.Sort((a, b) => a.DistanceSq.CompareTo(b.DistanceSq));
        _queueIndex = 0;

        // Короткое «ускоренное» окно на новом месте.
        _entryUntil = PerfData.Now + (long)(ResortEntryWindowSeconds * Stopwatch.Frequency);

        Log?.Invoke($"World preload re-centered on player (section {px},{py}); {FramedSections}/{Total} sections framed so far");
    }

    private static bool StartNextSection()
    {
        while (_queueIndex < _queue.Count)
        {
            var section = _queue[_queueIndex++];

            if (!Main.sectionManager.SectionLoaded(section.X, section.Y))
            {
                // v0.8.10: секция ещё не пришла с сервера — перепланируем
                // её на повторный проход (Finish поставит таймер).
                _skippedNotLoaded++;
                continue;
            }
            if (Main.sectionManager.SectionFramed(section.X, section.Y))
                continue;

            WorldGen.GetSectionBounds(section.X, section.Y, section.X, section.Y,
                out int tileStartX, out int tileEndX, out int tileStartY, out int tileEndY);

            _curX = section.X;
            _curY = section.Y;
            _tileX = tileStartX - 1;
            _tileStartY = tileStartY - 1;
            _tileY = _tileStartY;
            _tileEndX = tileEndX + 1;
            _tileEndY = tileEndY + 1;
            _sectionTileDone = 0;
            _sectionTileTotal = Math.Max(1, (_tileEndX - _tileX) * (_tileEndY - _tileStartY));
            _sectionMs = 0.0;
            _sectionActive = true;
            return true;
        }

        return false;
    }

    private static void AddWork(double ms)
    {
        if (ms <= 0.0)
            return;

        TotalMs += ms;
        _sectionMs += ms;
    }

    private static void Finish()
    {
        _wallSeconds = _startedAt != 0 ? (PerfData.Now - _startedAt) / (double)Stopwatch.Frequency : 0.0;
        Active = false;
        EndNoMapUpdate();

        if (FramedSections > 0)
            Log?.Invoke($"World preload finished: {FramedSections}/{Total} sections framed in {TotalMs:0} ms of work ({Seconds:0.0} s wall), avg {AverageSectionMs:0.0} ms, max {MaxSectionMs:0.0} ms");

        // v0.8.10: мультиплеер — что-то ещё не загружено с сервера;
        // вернёмся к этому, когда секции доедут (раз в RetryDelaySeconds).
        if (_skippedNotLoaded > 0 && Main.netMode != 2 && !Main.dedServ && !Failed)
        {
            _retryAt = PerfData.Now + (long)(RetryDelaySeconds * Stopwatch.Frequency);
            if (FramedSections > 0 || _skippedNotLoaded != Total)
                Log?.Invoke($"World preload: {_skippedNotLoaded} sections are not loaded yet (multiplayer), re-scan in {(int)RetryDelaySeconds} s");
            _skippedNotLoaded = 0;
        }
        else
        {
            _skippedNotLoaded = 0;
        }
    }

    private static void ResetSectionCursor()
    {
        _sectionActive = false;
        _curX = 0;
        _curY = 0;
        _tileX = 0;
        _tileY = 0;
        _tileStartY = 0;
        _tileEndX = 0;
        _tileEndY = 0;
        _sectionTileDone = 0;
        _sectionTileTotal = 0;
        _sectionMs = 0.0;
    }

    // Пока идёт предзагрузка — не трогаем миникарту (она бы пересчитывала
    // каждый Reframe).
    private static void BeginNoMapUpdate()
    {
        _noMapUpdateSet = true;
        Main.mapTime = Main.mapTimeMax + 10;
        WorldGen.noMapUpdate = true;
    }

    private static void EndNoMapUpdate()
    {
        if (!_noMapUpdateSet)
            return;

        _noMapUpdateSet = false;
        WorldGen.noMapUpdate = false;
    }

    // Босс ближе 6000 пикселей (~375 тайлов) — предзагрузку пропускаем.
    private static bool BossNearby()
    {
        var player = Main.LocalPlayer;
        if (player == null)
            return false;

        float px = player.Center.X;
        float py = player.Center.Y;

        var npcs = Main.npc;
        if (npcs == null)
            return false;

        for (int i = 0; i < npcs.Length; i++)
        {
            var npc = npcs[i];
            if (npc == null || !npc.active || !npc.boss || npc.friendly || npc.dontTakeDamage)
                continue;

            float dx = npc.Center.X - px;
            float dy = npc.Center.Y - py;
            if (dx * dx + dy * dy <= 36000000f)
                return true;
        }

        return false;
    }
}
