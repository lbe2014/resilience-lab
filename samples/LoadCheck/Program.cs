using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using ResilienceLab;

var requests = args.Length > 0 ? int.Parse(args[0]) : 256;
var concurrency = args.Length > 1 ? int.Parse(args[1]) : 32;
var output = args.Length > 2 ? args[2] : "artifacts/load-check";
if (requests is < 16 or > 100_000 || concurrency is < 8 or > 256)
    throw new ArgumentOutOfRangeException(nameof(args), "Requests: 16..100000; concurrency: 8..256.");
using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));
using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5099"), Timeout = System.Threading.Timeout.InfiniteTimeSpan };
var telemetry = new ResilienceTelemetry("load-api");
var tracesStarted = 0;
var tracesStopped = 0;
using var listener = new ActivityListener
{
    ShouldListenTo = source => source.Name == ResilienceTelemetry.ActivitySourceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    ActivityStarted = _ => Interlocked.Increment(ref tracesStarted),
    ActivityStopped = _ => Interlocked.Increment(ref tracesStopped)
};
ActivitySource.AddActivityListener(listener);
var reports = new List<Phase>();
var checks = new List<string>();
var clientStarted = 0;
var clientFinished = 0;

async Task<string> Call(string mode, int attempt, CancellationToken token)
{
    Interlocked.Increment(ref clientStarted);
    try
    {
        using var response = await http.GetAsync($"/load/{mode}?attempt={attempt}", HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token);
    }
    finally { Interlocked.Increment(ref clientFinished); }
}

async Task<ServerStats> WaitForDrain()
{
    for (var index = 0; index < 250; index++)
    {
        var stats = await http.GetFromJsonAsync<ServerStats>("/load/stats", budget.Token) ?? throw new InvalidOperationException("Missing stats");
        if (stats.Active == 0 && stats.Started == stats.Finished) return stats;
        await Task.Delay(20, budget.Token);
    }
    throw new InvalidOperationException("Server handlers did not drain within five seconds.");
}

async Task<Phase> Run(string name, Func<int, CancellationToken, Task<string>> operation)
{
    await WaitForDrain();
    using (var reset = await http.PostAsync("/load/reset", null, budget.Token)) reset.EnsureSuccessStatusCode();
    var before = clientStarted;
    var errors = new ConcurrentDictionary<string, int>();
    var latencies = new double[requests];
    var successes = 0;
    var elapsed = Stopwatch.StartNew();
    await Parallel.ForEachAsync(Enumerable.Range(0, requests), new ParallelOptions
    { MaxDegreeOfParallelism = concurrency, CancellationToken = budget.Token }, async (index, token) =>
    {
        var started = Stopwatch.GetTimestamp();
        try { await operation(index, token); Interlocked.Increment(ref successes); }
        catch (Exception error) when (error is not OperationCanceledException)
        { errors.AddOrUpdate(error.GetType().Name, 1, (_, count) => count + 1); }
        finally { latencies[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    });
    elapsed.Stop();
    var stats = await WaitForDrain();
    Array.Sort(latencies);
    double Percentile(double quantile) => latencies[(int)Math.Ceiling(requests * quantile) - 1];
    var phase = new Phase(name, requests, successes, errors.ToDictionary(), clientStarted - before,
        stats, elapsed.Elapsed.TotalSeconds, requests / elapsed.Elapsed.TotalSeconds,
        Percentile(.50), Percentile(.95), Percentile(.99));
    reports.Add(phase);
    Console.WriteLine($"{name}: success={successes}/{requests}, attempts={phase.ClientAttempts}, peak={stats.Peak}, p95={phase.P95Ms:F1}ms");
    return phase;
}

void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + description);
    checks.Add(description);
    Console.WriteLine("PASS " + description);
}

var retryOptions = new RetryOptions
{
    MaxRetries = 2, BaseDelay = TimeSpan.FromMilliseconds(5), MaxDelay = TimeSpan.FromMilliseconds(10),
    UseJitter = false, ShouldRetry = error => error is HttpRequestException
};
var retry = new ResiliencePipelineBuilder<string>().WithTelemetry(telemetry).AddRetry(retryOptions).Build();
try
{
    using (var health = await http.GetAsync("/health", budget.Token)) health.EnsureSuccessStatusCode();
    var healthy = await Run("healthy", (_, token) => Call("healthy", 0, token));
    Check(healthy.Successes == requests && healthy.ClientAttempts == requests, "Healthy calls execute once");

    var flaky = await Run("retry-recovery", (_, token) =>
    {
        var attempt = 0;
        return retry.ExecuteAsync(ct => Call("flaky", attempt++, ct), token);
    });
    Check(flaky.Successes == requests && flaky.ClientAttempts == requests * 3, "Retry recovers with exactly three attempts per call");

    var down = await Run("sustained-failure", (_, token) => retry.ExecuteAsync(ct => Call("down", 0, ct), token));
    Check(down.Successes == 0 && down.ClientAttempts == requests * 3, "Sustained failure respects the retry budget");

    var circuit = new CircuitBreaker(new CircuitBreakerOptions
    {
        FailureThreshold = 2, BreakDuration = TimeSpan.FromSeconds(5),
        ShouldHandle = error => error is HttpRequestException, Telemetry = telemetry
    });
    // Open before the burst, so this phase measures protection during an established outage.
    for (var index = 0; index < 2; index++)
        try { await circuit.ExecuteAsync(ct => retry.ExecuteAsync(inner => Call("down", 0, inner), ct), budget.Token); }
        catch (HttpRequestException) { }
    var protectedDown = await Run("open-circuit", (_, token) => circuit.ExecuteAsync(
        ct => retry.ExecuteAsync(inner => Call("down", 0, inner), ct), token));
    Check(protectedDown.ClientAttempts < down.ClientAttempts && protectedDown.Errors.GetValueOrDefault(nameof(BrokenCircuitException)) > 0,
        "Open circuit rejects calls and reduces dependency traffic");
    while (circuit.State == CircuitState.Open) await Task.Delay(20, budget.Token);
    await circuit.ExecuteAsync(ct => Call("healthy", 0, ct), budget.Token);
    var recovered = await Run("circuit-recovery", (_, token) => circuit.ExecuteAsync(ct => Call("healthy", 0, ct), token));
    Check(recovered.Successes == requests && circuit.State == CircuitState.Closed, "Circuit closes after a successful recovery probe");

    using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 4, QueueLimit = 0 });
    var limited = new ResiliencePipelineBuilder<string>().WithTelemetry(telemetry).AddRateLimit(limiter).Build();
    var rate = await Run("rate-limit", (_, token) => limited.ExecuteAsync(ct => Call("healthy", 0, ct), token));
    Check(rate.Server.Peak <= 4 && rate.Successes > 0 && rate.Errors.GetValueOrDefault(nameof(RateLimitRejectedException)) > 0
        && limiter.GetStatistics()!.CurrentAvailablePermits == 4,
        "Concurrency limiter caps handlers at four, rejects overflow and releases permits");

    var timeout = new ResiliencePipelineBuilder<string>().WithTelemetry(telemetry).AddTimeout(TimeSpan.FromMilliseconds(50)).Build();
    var timed = await Run("timeout", (_, token) => timeout.ExecuteAsync(ct => Call("slow", 0, ct), token));
    Check(timed.Errors.GetValueOrDefault(nameof(TimeoutRejectedException)) == requests && timed.Server.Active == 0,
        "Timeouts settle and server handlers drain");

    var hedging = new ResiliencePipelineBuilder<string>().WithTelemetry(telemetry)
        .AddHedging(new HedgingOptions<string> { MaxHedgedAttempts = 1, Delay = TimeSpan.FromMilliseconds(10) }).Build();
    var hedged = await Run("hedging", (_, token) =>
    {
        var attempt = -1;
        return hedging.ExecuteAsync(ct => Call("hedge", Interlocked.Increment(ref attempt), ct), token);
    });
    Check(hedged.Successes == requests && hedged.ClientAttempts <= requests * 2 && hedged.Server.Active == 0,
        "Hedging completes within its attempt budget and drains server handlers");
    Check(clientStarted == clientFinished, "All client attempt tasks have settled");
    Check(tracesStarted == tracesStopped, "All resilience spans have stopped");
}
finally
{
    Directory.CreateDirectory(output);
    var report = new
    {
        generatedAt = DateTimeOffset.UtcNow, runtime = Environment.Version.ToString(),
        os = Environment.OSVersion.ToString(), processors = Environment.ProcessorCount,
        passed = checks.Count == 10,
        requests, concurrency, tracing = "ActivityListener, AllDataAndRecorded; counts only",
        clientStarted, clientFinished, tracesStarted, tracesStopped,
        checks, phases = reports,
        limitations = "Local bounded workload, shared machine; not a production throughput guarantee or memory leak proof."
    };
    await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    var lines = new List<string>
    {
        "# Carga local de ResilienceLab", "", $"Solicitudes por fase: {requests}; concurrencia: {concurrency}. Tracing activado.", "",
        "| Fase | Éxitos | Intentos | Pico servidor | p50 ms | p95 ms | p99 ms | Llamadas/s |",
        "|---|---:|---:|---:|---:|---:|---:|---:|"
    };
    lines.AddRange(reports.Select(phase => FormattableString.Invariant(
        $"| {phase.Name} | {phase.Successes}/{phase.Requests} | {phase.ClientAttempts} | {phase.Server.Peak} | {phase.P50Ms:F1} | {phase.P95Ms:F1} | {phase.P99Ms:F1} | {phase.CallsPerSecond:F1} |")));
    lines.AddRange(["", $"Comprobaciones aprobadas: {checks.Count}/10.", "", "Los percentiles incluyen rechazos inmediatos; compara el resultado y errores de cada fase. No equivalen a capacidad de producción ni prueban ausencia de fugas de memoria."]);
    await File.WriteAllLinesAsync(Path.Combine(output, "report.md"), lines);
}

internal sealed record ServerStats(int Active, int Peak, int Started, int Finished, int Canceled);
internal sealed record Phase(string Name, int Requests, int Successes, Dictionary<string, int> Errors,
    int ClientAttempts, ServerStats Server, double Seconds, double CallsPerSecond, double P50Ms, double P95Ms, double P99Ms);
