using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using CatalogApi;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ResilienceLab;
using ResilienceLab.Http;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Catalog");
var baseUrl = new Uri(settings["BaseUrl"] ?? "https://dummyjson.com/");
if (baseUrl.Scheme is not ("http" or "https") || !baseUrl.AbsoluteUri.EndsWith('/'))
    throw new InvalidOperationException("Catalog:BaseUrl must be an HTTP(S) base address ending in /.");
int Bounded(string name, int fallback, int minimum, int maximum)
{
    var value = settings.GetValue(name, fallback);
    return value >= minimum && value <= maximum ? value
        : throw new InvalidOperationException($"Catalog:{name} must be between {minimum} and {maximum}.");
}
var timeout = TimeSpan.FromMilliseconds(Bounded("AttemptTimeoutMilliseconds", 2000, 50, 30000));
var retries = Bounded("MaxRetries", 2, 0, 5);
var threshold = Bounded("FailureThreshold", 2, 1, 20);
var breakDuration = TimeSpan.FromSeconds(Bounded("BreakSeconds", 15, 1, 300));
var maxStale = TimeSpan.FromSeconds(Bounded("MaxStaleSeconds", 300, 1, 3600));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CatalogCache>();
builder.Services.AddTransient<CatalogService>();
builder.Services.AddResilientHttpClient(client =>
{
    client.BaseAddress = baseUrl;
    client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
    client.MaxResponseContentBufferSize = 1024 * 1024;
}, provider => new HttpRetryOptions
{
    MaxRetries = 0, // Only the pipeline owns the retry budget.
    Telemetry = new ResilienceTelemetry("catalog-upstream",
        provider.GetRequiredService<ILoggerFactory>().CreateLogger("Resilience"))
});
builder.Services.AddResiliencePipeline<CatalogSnapshot>("catalog", (provider, pipeline) =>
{
    var cache = provider.GetRequiredService<CatalogCache>();
    pipeline.WithTelemetry(new ResilienceTelemetry("catalog-upstream",
        provider.GetRequiredService<ILoggerFactory>().CreateLogger("Resilience")))
        .AddFallback((outcome, token) =>
        {
            token.ThrowIfCancellationRequested();
            var cached = cache.Read(maxStale);
            if (cached is not null) return Task.FromResult(cached);
            // No fabricated seed: a cold or expired cache means real unavailability.
            ExceptionDispatchInfo.Capture(outcome.Exception!).Throw();
            throw new UnreachableException();
        }, new FallbackOptions<CatalogSnapshot>
        {
            ShouldHandleException = error => CatalogFailures.IsTransient(error) || error is BrokenCircuitException
        })
        .AddCircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = threshold, BreakDuration = breakDuration,
            ShouldHandle = CatalogFailures.IsTransient
        })
        .AddRetry(new RetryOptions
        {
            MaxRetries = retries, BaseDelay = TimeSpan.FromMilliseconds(100),
            MaxDelay = TimeSpan.FromSeconds(1), ShouldRetry = CatalogFailures.IsTransient
        })
        .AddTimeout(timeout);
});

var console = builder.Configuration.GetValue("Telemetry:Console", false);
var otlp = builder.Configuration.GetValue("Telemetry:OtlpEnabled", false);
var endpoint = new Uri(builder.Configuration["Telemetry:Endpoint"] ?? "http://127.0.0.1:4318");
builder.Services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService("ResilienceLab.CatalogApi"))
    .WithTracing(tracing =>
    {
        tracing.AddSource(ResilienceTelemetry.ActivitySourceName).AddAspNetCoreInstrumentation();
        if (console) tracing.AddConsoleExporter();
        if (otlp) tracing.AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri(endpoint, "/v1/traces");
            options.Protocol = OtlpExportProtocol.HttpProtobuf;
        });
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(ResilienceTelemetry.MeterName);
        if (otlp) metrics.AddOtlpExporter(options =>
        {
            options.Endpoint = new Uri(endpoint, "/v1/metrics");
            options.Protocol = OtlpExportProtocol.HttpProtobuf;
        });
    });

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapGet("/catalog", async (
    [FromKeyedServices("catalog")] ResiliencePipeline<CatalogSnapshot> pipeline,
    CatalogService catalog, HttpContext context, CancellationToken token) =>
{
    try
    {
        var result = await pipeline.ExecuteAsync(catalog.FetchAsync, token);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(result);
    }
    catch (Exception error) when (CatalogFailures.IsTransient(error) || error is BrokenCircuitException)
    {
        return Results.Problem(statusCode: 503, title: "Catálogo temporalmente no disponible",
            extensions: new Dictionary<string, object?> { ["traceId"] = Activity.Current?.TraceId.ToString() });
    }
    catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException)
    {
        return Results.Problem(statusCode: 502, title: "Respuesta de catálogo no válida");
    }
});
app.Run();
