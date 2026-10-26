using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace CalcApp.Services;

public sealed record LoadTestRequest(int Requests = 1000, int Concurrency = 20, string Target = "calc", int DurationSeconds = 0);

/// <summary>
/// Server-side HTTP load generator: the app calls its OWN API over real HTTP (loopback),
/// so every call is a real web transaction (visible to APM tools, logs, metrics).
/// </summary>
public sealed class LoadTestService
{
    public const int MaxRequests = 1_000_000;
    public const int MaxConcurrency = 500;
    public const int MaxDurationSeconds = 600;
    public static readonly string[] Targets = ["calc", "eval", "slow", "cpu", "error", "mixed"];

    private readonly IServer _server;
    private readonly IConfiguration _config;
    private readonly ILogger<LoadTestService> _log;
    private readonly object _lock = new();
    private LoadTestJob? _current;

    public LoadTestService(IServer server, IConfiguration config, ILogger<LoadTestService> log)
    {
        _server = server; _config = config; _log = log;
    }

    public object? Current
    {
        get { lock (_lock) return _current?.ToDto(); }
    }

    public object Stop()
    {
        lock (_lock)
        {
            _current?.Cancel();
            return _current?.ToDto() ?? new { running = false };
        }
    }

    public object Start(LoadTestRequest req)
    {
        var target = (req.Target ?? "calc").ToLowerInvariant();
        if (!Targets.Contains(target)) throw new CalcException($"Unknown target '{req.Target}'. Use: {string.Join(", ", Targets)}");

        var duration = Math.Clamp(req.DurationSeconds, 0, MaxDurationSeconds);
        var requests = duration > 0 ? int.MaxValue : Math.Clamp(req.Requests, 1, MaxRequests);
        var concurrency = Math.Clamp(req.Concurrency, 1, MaxConcurrency);
        var baseUrl = ResolveBaseUrl();

        lock (_lock)
        {
            if (_current is { Running: true }) throw new CalcException("A load test is already running. Stop it first.");
            var job = new LoadTestJob(target, requests, concurrency, duration, baseUrl);
            _current = job;
            _ = Task.Run(() => RunAsync(job));
            _log.LogInformation("Load test started: target={Target} requests={Requests} concurrency={Concurrency} duration={Duration}s base={Base}",
                target, duration > 0 ? "unlimited" : requests.ToString(), concurrency, duration, baseUrl);
            return job.ToDto();
        }
    }

    /// <summary>Loopback URL of this app (works inside Docker: http://[::]:8080 -> http://127.0.0.1:8080).</summary>
    private string ResolveBaseUrl()
    {
        var configured = _config["LOADTEST_BASEURL"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');

        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var addr = addresses.FirstOrDefault(a => a.StartsWith("http://")) ?? addresses.FirstOrDefault() ?? "http://127.0.0.1:8080";
        var uri = new Uri(addr.Replace("://+", "://127.0.0.1").Replace("://*", "://127.0.0.1").Replace("://[::]", "://127.0.0.1").Replace("://0.0.0.0", "://127.0.0.1"));
        return $"{uri.Scheme}://{(uri.Host == "localhost" ? "127.0.0.1" : uri.Host)}:{uri.Port}";
    }

    private async Task RunAsync(LoadTestJob job)
    {
        using var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = job.Concurrency,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseProxy = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } // loopback only
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(job.BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CalcApp-LoadTest/1.0");

        long issued = 0;
        var workers = Enumerable.Range(0, job.Concurrency).Select(async _ =>
        {
            var rnd = new Random();
            while (!job.Token.IsCancellationRequested)
            {
                if (Interlocked.Increment(ref issued) > job.Requests) break;
                var sw = Stopwatch.StartNew();
                var ok = false;
                try
                {
                    using var resp = await SendAsync(http, job.Target, rnd, job.Token);
                    ok = resp.IsSuccessStatusCode;
                }
                catch (OperationCanceledException) when (job.Token.IsCancellationRequested) { break; }
                catch { ok = false; }
                job.Record(ok, sw.Elapsed.TotalMilliseconds);
            }
        }).ToArray();

        try { await Task.WhenAll(workers); }
        catch (Exception ex) { _log.LogWarning(ex, "Load test worker failed"); }
        job.Finish();
        _log.LogInformation("Load test finished: {Completed} requests, {Errors} errors", job.Completed, job.Errors);
    }

    private static readonly string[] Ops = ["add", "sub", "mul", "div", "pow", "mod"];
    private static readonly string[] Expressions =
        ["(2+3)*4", "sqrt(144)+2^10", "sin(pi/2)+cos(0)", "10!/(5!*5!)", "ln(e^3)*log(1000)", "((1.5+2.5)*3-4)/2", "abs(-42)%5"];

    private static Task<HttpResponseMessage> SendAsync(HttpClient http, string target, Random rnd, CancellationToken ct)
    {
        if (target == "mixed")
        {
            var r = rnd.Next(100);
            target = r < 50 ? "calc" : r < 75 ? "eval" : r < 87 ? "slow" : r < 97 ? "cpu" : "error";
        }

        return target switch
        {
            "calc" => http.GetAsync($"/api/calc?a={rnd.Next(1, 1000)}&b={rnd.Next(1, 100)}&op={Ops[rnd.Next(Ops.Length)]}", ct),
            "eval" => http.PostAsJsonAsync("/api/calc/eval", new { expression = Expressions[rnd.Next(Expressions.Length)] }, ct),
            "slow" => http.GetAsync($"/api/slow?ms={rnd.Next(100, 500)}", ct),
            "cpu" => http.GetAsync($"/api/stress/cpu-work?ms={rnd.Next(20, 80)}", ct),
            "error" => http.GetAsync("/api/error", ct),
            _ => http.GetAsync("/api/health", ct)
        };
    }

    private sealed class LoadTestJob
    {
        private readonly CancellationTokenSource _cts;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _lock = new();
        private readonly List<double> _latencies = new();   // capped sample for percentiles
        private const int MaxSamples = 200_000;
        private long _completed, _errors;
        private double _totalMs, _maxMs;
        private double? _finishedSeconds;

        public LoadTestJob(string target, int requests, int concurrency, int durationSeconds, string baseUrl)
        {
            Target = target; Requests = requests; Concurrency = concurrency; DurationSeconds = durationSeconds; BaseUrl = baseUrl;
            _cts = durationSeconds > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds)) : new CancellationTokenSource();
        }

        public string Target { get; }
        public int Requests { get; }
        public int Concurrency { get; }
        public int DurationSeconds { get; }
        public string BaseUrl { get; }
        public CancellationToken Token => _cts.Token;
        public bool Running => _finishedSeconds is null;
        public long Completed => Interlocked.Read(ref _completed);
        public long Errors => Interlocked.Read(ref _errors);

        public void Cancel() => _cts.Cancel();
        public void Finish() { lock (_lock) _finishedSeconds ??= _clock.Elapsed.TotalSeconds; }

        public void Record(bool ok, double ms)
        {
            lock (_lock)
            {
                _completed++;
                if (!ok) _errors++;
                _totalMs += ms;
                if (ms > _maxMs) _maxMs = ms;
                if (_latencies.Count < MaxSamples) _latencies.Add(ms);
            }
        }

        public object ToDto()
        {
            lock (_lock)
            {
                var elapsed = _finishedSeconds ?? _clock.Elapsed.TotalSeconds;
                double P(double q)
                {
                    if (_latencies.Count == 0) return 0;
                    var sorted = _latencies.OrderBy(x => x).ToArray();
                    var idx = (int)Math.Ceiling(q * sorted.Length) - 1;
                    return Math.Round(sorted[Math.Clamp(idx, 0, sorted.Length - 1)], 2);
                }
                return new
                {
                    running = Running,
                    target = Target,
                    concurrency = Concurrency,
                    requests = DurationSeconds > 0 ? (int?)null : Requests,
                    durationSeconds = DurationSeconds,
                    baseUrl = BaseUrl,
                    elapsedSeconds = Math.Round(elapsed, 1),
                    completed = _completed,
                    errors = _errors,
                    requestsPerSecond = elapsed > 0 ? Math.Round(_completed / elapsed, 1) : 0,
                    avgMs = _completed == 0 ? 0 : Math.Round(_totalMs / _completed, 2),
                    p50Ms = P(0.50),
                    p95Ms = P(0.95),
                    p99Ms = P(0.99),
                    maxMs = Math.Round(_maxMs, 2)
                };
            }
        }
    }
}
