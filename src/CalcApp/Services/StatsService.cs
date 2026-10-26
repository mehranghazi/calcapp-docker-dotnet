using System.Collections.Concurrent;
using System.Diagnostics;

namespace CalcApp.Services;

/// <summary>Request counters (per endpoint) + process metrics for the live dashboard.</summary>
public sealed class StatsService
{
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly ConcurrentDictionary<string, EndpointCounter> _endpoints = new();
    private long _total, _errors, _inFlight;

    // CPU % sampling
    private readonly object _cpuLock = new();
    private TimeSpan _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;
    private DateTime _lastSample = DateTime.UtcNow;
    private double _lastCpuPercent;

    public void Begin() => Interlocked.Increment(ref _inFlight);

    public void End(string endpoint, int status, double ms)
    {
        Interlocked.Decrement(ref _inFlight);
        Interlocked.Increment(ref _total);
        if (status >= 500) Interlocked.Increment(ref _errors);
        var c = _endpoints.GetOrAdd(endpoint, _ => new EndpointCounter());
        c.Add(status, ms);
    }

    public void Reset()
    {
        _endpoints.Clear();
        Interlocked.Exchange(ref _total, 0);
        Interlocked.Exchange(ref _errors, 0);
    }

    private double CpuPercent()
    {
        lock (_cpuLock)
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastSample).TotalMilliseconds;
            if (elapsed < 500) return _lastCpuPercent; // avoid noisy samples
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            var used = (cpu - _lastCpu).TotalMilliseconds;
            _lastCpuPercent = Math.Round(Math.Clamp(used / (elapsed * Environment.ProcessorCount) * 100, 0, 100), 1);
            _lastCpu = cpu;
            _lastSample = now;
            return _lastCpuPercent;
        }
    }

    public object Snapshot()
    {
        var p = Process.GetCurrentProcess();
        ThreadPool.GetAvailableThreads(out var availWorkers, out _);
        ThreadPool.GetMaxThreads(out var maxWorkers, out _);
        var gc = GC.GetGCMemoryInfo();

        return new
        {
            machine = Environment.MachineName,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            processorCount = Environment.ProcessorCount,
            uptimeSeconds = (long)(DateTime.UtcNow - _startedUtc).TotalSeconds,
            cpuPercent = CpuPercent(),
            workingSetMb = Math.Round(p.WorkingSet64 / 1048576.0, 1),
            gcHeapMb = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
            memoryLimitMb = Math.Round(gc.TotalAvailableMemoryBytes / 1048576.0, 0),
            gcCollections = new { gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2) },
            threads = p.Threads.Count,
            threadPoolBusy = maxWorkers - availWorkers,
            requests = new
            {
                total = Interlocked.Read(ref _total),
                errors = Interlocked.Read(ref _errors),
                inFlight = Interlocked.Read(ref _inFlight),
                endpoints = _endpoints
                    .OrderByDescending(kv => kv.Value.Count)
                    .Select(kv => kv.Value.ToDto(kv.Key))
                    .ToList()
            }
        };
    }

    private sealed class EndpointCounter
    {
        private long _count, _errors;
        private double _totalMs, _maxMs;
        private readonly object _lock = new();

        public long Count => Interlocked.Read(ref _count);

        public void Add(int status, double ms)
        {
            lock (_lock)
            {
                _count++;
                if (status >= 500) _errors++;
                _totalMs += ms;
                if (ms > _maxMs) _maxMs = ms;
            }
        }

        public object ToDto(string name)
        {
            lock (_lock)
            {
                return new
                {
                    endpoint = name,
                    count = _count,
                    errors = _errors,
                    avgMs = _count == 0 ? 0 : Math.Round(_totalMs / _count, 2),
                    maxMs = Math.Round(_maxMs, 2)
                };
            }
        }
    }
}
