using System.Diagnostics;
using System.Globalization;
using CalcApp.Services;

// Docker HEALTHCHECK: `dotnet CalcApp.dll --healthcheck` (the runtime image has no curl/wget)
if (args.Contains("--healthcheck"))
{
    var port = (Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080").Split(';', ',')[0].Trim();
    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var r = await http.GetAsync($"http://127.0.0.1:{port}/health");
        return r.IsSuccessStatusCode ? 0 : 1;
    }
    catch
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<StatsService>();
builder.Services.AddSingleton<StressService>();
builder.Services.AddSingleton<LoadTestService>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();

// ---- request statistics (feeds the live dashboard) ----
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "/";
    var isApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/api/stats", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/api/loadtest", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/api/stress/status", StringComparison.OrdinalIgnoreCase);
    if (!isApi) { await next(); return; }

    var stats = ctx.RequestServices.GetRequiredService<StatsService>();
    var sw = Stopwatch.StartNew();
    stats.Begin();
    var status = 500;
    try
    {
        await next();
        status = ctx.Response.StatusCode;
    }
    finally
    {
        stats.End($"{ctx.Request.Method} {path.ToLowerInvariant()}", status, sw.Elapsed.TotalMilliseconds);
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

// ===================== Calculator =====================

// GET /api/calc?a=5&b=3&op=add      ops: add sub mul div pow mod
app.MapGet("/api/calc", (double a, double b, string op) =>
{
    double result;
    switch (op.ToLowerInvariant())
    {
        case "add": case "+": result = a + b; break;
        case "sub": case "-": result = a - b; break;
        case "mul": case "*": result = a * b; break;
        case "div": case "/":
            if (b == 0) return Results.BadRequest(new { error = "Division by zero." });
            result = a / b; break;
        case "pow": case "^": result = Math.Pow(a, b); break;
        case "mod": case "%":
            if (b == 0) return Results.BadRequest(new { error = "Modulo by zero." });
            result = a % b; break;
        default:
            return Results.BadRequest(new { error = $"Unknown op '{op}'. Use add, sub, mul, div, pow, mod." });
    }
    if (double.IsInfinity(result) || double.IsNaN(result)) return Results.BadRequest(new { error = "Result is out of range." });
    return Results.Ok(new { a, b, op, result });
});

// POST /api/calc/eval   { "expression": "(2+3)*sqrt(16)" }
app.MapPost("/api/calc/eval", (EvalRequest req) =>
{
    try
    {
        var result = ExpressionEvaluator.Evaluate(req.Expression);
        return Results.Ok(new { expression = req.Expression, result, display = Format(result) });
    }
    catch (CalcException ex)
    {
        return Results.BadRequest(new { expression = req.Expression, error = ex.Message });
    }
});

// ===================== Test helpers (for load / APM tests) =====================

app.MapGet("/api/slow", async (int? ms) =>
{
    var delay = Math.Clamp(ms ?? 500, 0, 30000);
    await Task.Delay(delay);
    return Results.Ok(new { delayedMs = delay });
});

app.MapGet("/api/error", (int? code) =>
{
    var c = code is >= 400 and <= 599 ? code.Value : 500;
    if (c == 500) throw new InvalidOperationException("Simulated server error (for testing).");
    return Results.Problem(statusCode: c, title: "Simulated error");
});

app.MapGet("/api/stress/cpu-work", (int? ms) =>
{
    var work = Math.Clamp(ms ?? 50, 1, 5000);
    var x = StressService.CpuWork(work);
    return Results.Ok(new { cpuMs = work, checksum = Math.Round(x % 1000, 3) });
});

// Background stress jobs
app.MapPost("/api/stress/cpu", (StressService s, int? seconds, int? threads) =>
    Results.Ok(s.StartCpu(seconds ?? 30, threads ?? Environment.ProcessorCount)));

app.MapPost("/api/stress/memory", (StressService s, int? mb, int? seconds) =>
    Results.Ok(s.StartMemory(mb ?? 256, seconds ?? 30)));

app.MapPost("/api/stress/stop", (StressService s) => Results.Ok(s.Stop()));
app.MapGet("/api/stress/status", (StressService s) => Results.Ok(s.Status()));

// HTTP load test (the app calls its own API)
app.MapPost("/api/loadtest/start", (LoadTestService lt, LoadTestRequest req) =>
{
    try { return Results.Ok(lt.Start(req)); }
    catch (CalcException ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapGet("/api/loadtest/status", (LoadTestService lt) => Results.Ok(lt.Current ?? new { running = false }));
app.MapPost("/api/loadtest/stop", (LoadTestService lt) => Results.Ok(lt.Stop()));

// ===================== Monitoring =====================

app.MapGet("/api/stats", (StatsService s) => Results.Ok(s.Snapshot()));
app.MapPost("/api/stats/reset", (StatsService s) => { s.Reset(); return Results.Ok(new { reset = true }); });

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", time = DateTime.UtcNow }));
app.MapGet("/api/health", () => Results.Ok(new { status = "Healthy", time = DateTime.UtcNow }));

app.Run();
return 0;

static string Format(double v) =>
    Math.Abs(v) >= 1e15 || (v != 0 && Math.Abs(v) < 1e-9)
        ? v.ToString("G12", CultureInfo.InvariantCulture)
        : Math.Round(v, 10).ToString("0.##########", CultureInfo.InvariantCulture);

public sealed record EvalRequest(string Expression);
