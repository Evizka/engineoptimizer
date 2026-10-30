using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace EngineOptimizer.Perf;

// Фоновая запись файлов телеметрии: текст собирается на игровом потоке,
// а диск трогает один низкоприоритетный фоновый поток. Без этого запись
// live.json/CSV давала рывок 5-50 мс, особенно с антивирусом.
public static class AsyncFileWriter
{
    private const int QueueLimit = 256;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly object Gate = new object();
    private static readonly Queue<Pending> Queue = new Queue<Pending>();
    private static readonly AutoResetEvent Signal = new AutoResetEvent(false);
    private static Worker _worker;
    private static int _inFlight;

    public static int Dropped;
    public static int Failed;

    private sealed class Worker
    {
        public volatile bool Stopping;
    }

    private struct Pending
    {
        public string Path;
        public string Text;
        public string NewFileHeader;
        public bool Atomic;
    }

    public static void Start()
    {
        lock (Gate)
        {
            if (_worker != null)
                return;

            var worker = new Worker();
            _worker = worker;

            var thread = new Thread(() => Loop(worker))
            {
                IsBackground = true,
                Name = "EngineOptimizer.Telemetry",
                Priority = ThreadPriority.BelowNormal
            };
            thread.Start();
        }
    }

    public static void WriteAtomic(string path, string text) =>
        Enqueue(new Pending { Path = path, Text = text, Atomic = true });

    public static void Append(string path, string text, string newFileHeader) =>
        Enqueue(new Pending { Path = path, Text = text, NewFileHeader = newFileHeader });

    public static void Flush(int timeoutMs)
    {
        Start();

        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            lock (Gate)
            {
                if (Queue.Count == 0 && _inFlight == 0)
                    return;
            }

            Thread.Sleep(4);

            if (Environment.TickCount64 >= deadline)
                return;
        }
    }

    public static void Stop()
    {
        Worker worker;

        lock (Gate)
        {
            worker = _worker;
            _worker = null;
        }

        if (worker == null)
            return;

        worker.Stopping = true;
        Signal.Set();
    }

    private static void Enqueue(Pending item)
    {
        Start();

        lock (Gate)
        {
            // Переполнение очереди (диск совсем застрял) — тихо выбрасываем,
            // телеметрия не должна ронять игру.
            if (Queue.Count >= QueueLimit)
            {
                Dropped++;
                return;
            }

            Queue.Enqueue(item);
        }

        Signal.Set();
    }

    private static void Loop(Worker worker)
    {
        while (true)
        {
            Pending item = default;
            bool hasItem = false;

            lock (Gate)
            {
                if (Queue.Count > 0)
                {
                    item = Queue.Dequeue();
                    hasItem = true;
                    _inFlight++;
                }
                else if (worker.Stopping)
                    return;
            }

            if (!hasItem)
            {
                Signal.WaitOne(200);
                continue;
            }

            try
            {
                Write(item);
            }
            catch
            {
                Failed++;
            }
            finally
            {
                lock (Gate)
                {
                    _inFlight--;
                }
            }
        }
    }

    private static void Write(Pending item)
    {
        string dir = Path.GetDirectoryName(item.Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (item.Atomic)
        {
            // Атомарная замена: пишем во временный файл и переименовываем,
            // чтобы читатель никогда не увидел половину файла.
            File.WriteAllText(item.Path + ".tmp", item.Text, Utf8NoBom);
            File.Move(item.Path + ".tmp", item.Path, true);
            return;
        }

        if (item.NewFileHeader != null)
        {
            var info = new FileInfo(item.Path);
            if (!info.Exists || info.Length == 0)
                File.AppendAllText(item.Path, item.NewFileHeader, Utf8NoBom);
        }

        File.AppendAllText(item.Path, item.Text, Utf8NoBom);
    }
}
