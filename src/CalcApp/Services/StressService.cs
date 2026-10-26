namespace CalcApp.Services;

/// <summary>Background CPU / memory stress jobs with hard safety limits.</summary>
public sealed class StressService
{
    public const int MaxSeconds = 300;
    public const int MaxMemoryMb = 4096;

    private readonly object _lock = new();
    private StressJob? _cpu, _memory;

    public static int MaxThreads => Environment.ProcessorCount * 4;

    public object StartCpu(int seconds, int threads)
    {
        seconds = Math.Clamp(seconds, 1, MaxSeconds);
        threads = Math.Clamp(threads, 1, MaxThreads);
        lock (_lock)
        {
            _cpu?.Cancel();
            var job = new StressJob("cpu", seconds, threads == 1 ? "1 thread" : $"{threads} threads");
            _cpu = job;
            for (var i = 0; i < threads; i++)
            {
                var t = new Thread(() => BurnCpu(job.Token)) { IsBackground = true, Name = $"stress-cpu-{i}" };
                t.Start();
            }
            return job.ToDto();
        }
    }

    public object StartMemory(int megabytes, int seconds)
    {
        megabytes = Math.Clamp(megabytes, 1, MaxMemoryMb);
        seconds = Math.Clamp(seconds, 1, MaxSeconds);
        lock (_lock)
        {
            _memory?.Cancel();
            var job = new StressJob("memory", seconds, $"{megabytes} MB");
            _memory = job;
            _ = Task.Run(async () =>
            {
                var blocks = new List<byte[]>();
                try
                {
                    for (var i = 0; i < megabytes && !job.Token.IsCancellationRequested; i++)
                    {
                        var block = new byte[1024 * 1024];
                        for (var j = 0; j < block.Length; j += 4096) block[j] = 1; // touch every page so it is really used
                        blocks.Add(block);
                    }
                    await Task.Delay(Timeout.Infinite, job.Token);
                }
                catch (OperationCanceledException) { }
                catch (OutOfMemoryException) { job.Note = $"Out of memory after {blocks.Count} MB - the container memory limit was reached. Memory was released."; job.Cancel(); }
                finally
                {
                    blocks.Clear();
                    GC.Collect();
                }
            });
            return job.ToDto();
        }
    }

    public object Stop()
    {
        lock (_lock)
        {
            _cpu?.Cancel();
            _memory?.Cancel();
            return Status();
        }
    }

    public object Status()
    {
        lock (_lock)
        {
            return new { cpu = _cpu?.ToDto(), memory = _memory?.ToDto() };
        }
    }

    /// <summary>Synchronous CPU work inside one request (used by the load test "cpu" target).</summary>
    public static double CpuWork(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(Math.Clamp(milliseconds, 1, 5000));
        double x = 0;
        while (DateTime.UtcNow < until)
            for (var i = 1; i < 20000; i++) x += Math.Sqrt(i) * Math.Sin(i);
        return x;
    }

    private static void BurnCpu(CancellationToken token)
    {
        double x = 0;
        while (!token.IsCancellationRequested)
            for (var i = 1; i < 100000; i++) x += Math.Sqrt(i) * Math.Sin(i);
        GC.KeepAlive(x);
    }

    private sealed class StressJob
    {
        private readonly CancellationTokenSource _cts;
        private readonly DateTime _started = DateTime.UtcNow;
        private readonly int _seconds;

        public StressJob(string kind, int seconds, string detail)
        {
            Kind = kind; Detail = detail; _seconds = seconds;
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            _cts.Token.Register(() => _ended = DateTime.UtcNow);
        }

        private DateTime? _ended;

        public string Kind { get; }
        public string Detail { get; }
        public string? Note { get; set; }
        public CancellationToken Token => _cts.Token;
        public void Cancel() => _cts.Cancel();

        public object ToDto()
        {
            var elapsed = ((_ended ?? DateTime.UtcNow) - _started).TotalSeconds;
            return new
            {
                kind = Kind,
                detail = Detail,
                running = !_cts.IsCancellationRequested,
                elapsedSeconds = Math.Round(Math.Min(elapsed, _seconds), 1),
                durationSeconds = _seconds,
                note = Note
            };
        }
    }
}
