using System.Collections.Concurrent;
using System.Diagnostics;
using ResilienceLab;
using ResilienceLab.Http;

var builder = WebApplication.CreateBuilder(args);
using var traces = new ActivityListener
{
    ShouldListenTo = source => source.Name == ResilienceTelemetry.ActivitySourceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => builder.Configuration.GetValue<bool>("Tracing:Console")
        ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
    ActivityStopped = activity => Console.WriteLine(
        $"[traza] {activity.OperationName} trace={activity.TraceId} span={activity.SpanId} parent={activity.ParentSpanId} status={activity.Status} duration={activity.Duration.TotalMilliseconds:F1}ms")
};
ActivitySource.AddActivityListener(traces);
// The consumer and simulated dependency share a local host for this sample.
const string address = "http://127.0.0.1:5099";
builder.WebHost.UseUrls(address);
builder.Services.AddResilientHttpClient(client =>
{
    client.BaseAddress = new Uri(address);
    client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
}, provider => new HttpRetryOptions
{
    MaxRetries = 2,
    BaseDelay = TimeSpan.FromMilliseconds(30),
    MaxDelay = TimeSpan.FromMilliseconds(100),
    UseJitter = false,
    Telemetry = new ResilienceTelemetry("fault-api", provider.GetRequiredService<ILoggerFactory>().CreateLogger("Resilience"))
});
var app = builder.Build();
var attempts = new ConcurrentDictionary<Guid, int>();
var telemetry = new ResilienceTelemetry("fault-api", app.Logger);
var circuit = new CircuitBreaker(new CircuitBreakerOptions
{
    FailureThreshold = 2,
    BreakDuration = TimeSpan.FromSeconds(3),
    ShouldHandle = error => error is HttpRequestException,
    Telemetry = telemetry
});

app.MapGet("/", () => Results.Ok(new
{
    scenarios = new[] { "/demo/retry", "/demo/timeout", "/demo/fallback", "/demo/circuit" },
    circuitRecoverySeconds = 3
}));
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
var loadStats = new LoadStats();
app.MapGet("/load/stats", () => Results.Ok(loadStats.Snapshot()));
app.MapPost("/load/reset", () => loadStats.Reset() ? Results.Ok() : Results.Conflict());
app.MapGet("/load/{mode}", async (string mode, int? attempt, HttpContext context) =>
{
    if (mode is not ("healthy" or "down" or "flaky" or "slow" or "hedge")) return Results.NotFound();
    loadStats.Enter();
    try
    {
        var delay = mode == "slow" || (mode == "hedge" && attempt == 0) ? 500 : 25;
        await Task.Delay(delay, context.RequestAborted);
        var failed = mode == "down" || (mode == "flaky" && attempt < 2);
        return Results.Text("load-result", statusCode: failed ? 503 : 200);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        loadStats.Canceled();
        throw;
    }
    finally { loadStats.Exit(); }
});

app.MapGet("/dependency/{mode}/{id:guid}", async (string mode, Guid id, HttpContext context) =>
{
    if (mode is not ("flaky" or "down" or "slow")) return Results.NotFound();
    // Bound state for direct exploratory calls; demo entries are removed on completion.
    if (attempts.Count >= 1024 && !attempts.ContainsKey(id)) return Results.StatusCode(429);
    var attempt = attempts.AddOrUpdate(id, 1, (_, value) => value + 1);
    if (mode == "slow") await Task.Delay(TimeSpan.FromSeconds(2), context.RequestAborted);
    if (mode == "down" || (mode == "flaky" && attempt <= 2))
        return Results.Json(new { attempt, status = "unavailable" }, statusCode: 503);
    return Results.Ok(new { attempt, status = "recovered" });
});

app.MapGet("/demo/{scenario}", async (string scenario, ResilientHttpClient client, HttpContext context) =>
{
    if (scenario is not ("retry" or "timeout" or "fallback" or "circuit")) return Results.NotFound();
    var id = Guid.NewGuid();
    async Task<string> Call(string mode, CancellationToken token)
    {
        using var response = await client.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"/dependency/{mode}/{id}"), token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token);
    }
    try
    {
        var body = scenario switch
        {
            "retry" => await Call("flaky", context.RequestAborted),
            "timeout" => await ResilienceLab.Timeout.ExecuteAsync(
                token => Call("slow", token),
                new TimeoutOptions { TimeoutDuration = TimeSpan.FromMilliseconds(100), Telemetry = telemetry },
                context.RequestAborted),
            "fallback" => await Fallback.ExecuteAsync(
                token => Call("down", token),
                (_, _) => Task.FromResult("cached-value"),
                new FallbackOptions<string> { ShouldHandleException = error => error is HttpRequestException, Telemetry = telemetry },
                context.RequestAborted),
            _ => await circuit.ExecuteAsync(token => Call("down", token), context.RequestAborted)
        };
        return Results.Ok(new { scenario, body, attempts = attempts.GetValueOrDefault(id), circuit = circuit.State.ToString() });
    }
    catch (TimeoutRejectedException)
    {
        return Results.Json(new { scenario, error = "timeout", attempts = attempts.GetValueOrDefault(id) }, statusCode: 504);
    }
    catch (BrokenCircuitException)
    {
        return Results.Json(new { scenario, error = "circuit-open", attempts = attempts.GetValueOrDefault(id) }, statusCode: 503);
    }
    catch (HttpRequestException)
    {
        return Results.Json(new { scenario, error = "dependency-unavailable", attempts = attempts.GetValueOrDefault(id), circuit = circuit.State.ToString() }, statusCode: 502);
    }
    finally
    {
        attempts.TryRemove(id, out _);
    }
});

app.Run();

// One bounded, shared counter set. No per-request identifiers are retained.
internal sealed class LoadStats
{
    private readonly object _gate = new();
    private int _active, _peak, _started, _finished, _canceled;
    public void Enter() { lock (_gate) { _started++; _active++; _peak = Math.Max(_peak, _active); } }
    public void Exit() { lock (_gate) { _finished++; _active--; } }
    public void Canceled() { lock (_gate) _canceled++; }
    public object Snapshot() { lock (_gate) return new { active = _active, peak = _peak, started = _started, finished = _finished, canceled = _canceled }; }
    public bool Reset()
    {
        lock (_gate)
        {
            if (_active != 0) return false;
            _peak = _started = _finished = _canceled = 0;
            return true;
        }
    }
}
