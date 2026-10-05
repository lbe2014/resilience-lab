using System.Diagnostics;
using ResilienceLab;
using ResilienceLab.Http;

var builder = WebApplication.CreateBuilder(args);
const string address = "http://127.0.0.1:5101";
builder.WebHost.UseUrls(address);
using var traces = new ActivityListener
{
    ShouldListenTo = source => source.Name == ResilienceTelemetry.ActivitySourceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    ActivityStopped = activity => Console.WriteLine(
        $"[traza] {activity.OperationName} trace={activity.TraceId} parent={activity.ParentSpanId} status={activity.Status}")
};
ActivitySource.AddActivityListener(traces);
builder.Services.AddResilientHttpClient(client =>
{
    client.BaseAddress = new Uri(address);
    client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
}, provider => new HttpRetryOptions
{
    // Retry belongs to the pipeline; avoid multiplying the HTTP budget.
    MaxRetries = 0,
    Telemetry = new ResilienceTelemetry("catalog-api", provider.GetRequiredService<ILoggerFactory>().CreateLogger("Resilience"))
});
builder.Services.AddResiliencePipeline<string>("catalog", (provider, pipeline) => pipeline
    .WithTelemetry(new ResilienceTelemetry("catalog-api", provider.GetRequiredService<ILoggerFactory>().CreateLogger("Resilience")))
    .AddFallback((_, _) => Task.FromResult("cached-value"), new FallbackOptions<string>
    {
        ShouldHandleException = error => error is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
    })
    .AddCircuitBreaker(new CircuitBreakerOptions
    {
        FailureThreshold = 2, BreakDuration = TimeSpan.FromSeconds(30),
        ShouldHandle = error => error is HttpRequestException
    })
    .AddRetry(new RetryOptions
    {
        MaxRetries = 2, BaseDelay = TimeSpan.FromMilliseconds(5), UseJitter = false,
        ShouldRetry = error => error is HttpRequestException
    })
    .AddTimeout(TimeSpan.FromSeconds(1)));

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapGet("/upstream/{mode}", async (string mode, int attempt, CancellationToken ct) =>
{
    if (mode == "slow") await Task.Delay(TimeSpan.FromSeconds(2), ct);
    return mode == "down" || (mode == "flaky" && attempt < 3)
        ? Results.StatusCode(503) : Results.Text("fresh-value");
});
app.MapGet("/catalog/{mode}", async (string mode,
    [Microsoft.Extensions.DependencyInjection.FromKeyedServices("catalog")] ResiliencePipeline<string> pipeline,
    ResilientHttpClient client, CancellationToken ct) =>
{
    if (mode is not ("healthy" or "flaky" or "slow" or "down")) return Results.NotFound();
    var attempts = 0;
    var body = await pipeline.ExecuteAsync(async token =>
    {
        var attempt = ++attempts;
        using var response = await client.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"/upstream/{mode}?attempt={attempt}"), token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token);
    }, ct);
    return Results.Ok(new { mode, body, attempts });
});
app.Run();
