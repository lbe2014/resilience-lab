using System.Net;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.RateLimiting;
using ResilienceLab;
using ResilienceLab.Http;
using ResilienceTimeout = ResilienceLab.Timeout;

using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options => options.SingleLine = true));
using var traces = new ActivityListener
{
    ShouldListenTo = source => source.Name == ResilienceTelemetry.ActivitySourceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    ActivityStopped = activity => Console.WriteLine(
        $"   [traza] {activity.OperationName} trace={activity.TraceId} span={activity.SpanId} parent={activity.ParentSpanId} status={activity.Status} duration={activity.Duration.TotalMilliseconds:F1}ms events={string.Join(',', activity.Events.Select(item => item.Name))}")
};
ActivitySource.AddActivityListener(traces);
using var metrics = new MeterListener();
metrics.InstrumentPublished = (instrument, listener) =>
{
    if (instrument.Meter.Name == ResilienceTelemetry.MeterName)
        listener.EnableMeasurementEvents(instrument);
};
metrics.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
    Console.WriteLine($"   [métrica] {instrument.Name} = {value} {instrument.Unit}"));
metrics.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
    Console.WriteLine($"   [métrica] {instrument.Name} = {value} {instrument.Unit}"));
metrics.Start();
var telemetry = new ResilienceTelemetry("demo-api", loggerFactory.CreateLogger("ResilienceLab.Demo"));

Console.WriteLine("1. Retry: dos fallos y recuperación");
var attempts = 0;
var result = await Retry.ExecuteAsync<string>(ct =>
{
    ct.ThrowIfCancellationRequested();
    Console.WriteLine($"   Intento {++attempts}");
    return attempts < 3
        ? Task.FromException<string>(new HttpRequestException("Fallo temporal"))
        : Task.FromResult("Operación completada");
}, new RetryOptions
{
    BaseDelay = TimeSpan.FromMilliseconds(10),
    UseJitter = false,
    Telemetry = telemetry,
    ShouldRetry = ex => ex is HttpRequestException,
    OnRetry = retry => Console.WriteLine($"   Reintento {retry.RetryNumber} en {retry.Delay.TotalMilliseconds} ms")
});
Console.WriteLine($"   {result}");

Console.WriteLine("2. Cancelación antes de esperar el backoff");
using (var cancel = new CancellationTokenSource())
{
    try
    {
        await Retry.ExecuteAsync<int>(_ => Task.FromException<int>(new HttpRequestException()),
            new RetryOptions
            {
                ShouldRetry = ex => ex is HttpRequestException,
                OnRetry = _ => cancel.Cancel()
            }, cancel.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("   Cancelado antes del siguiente intento");
    }
}

Console.WriteLine("3. Timeout cooperativo");
try
{
    await ResilienceTimeout.ExecuteAsync(async ct =>
    {
        await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
        return 1;
    }, new TimeoutOptions { TimeoutDuration = TimeSpan.FromMilliseconds(30), Telemetry = telemetry });
}
catch (TimeoutRejectedException)
{
    Console.WriteLine("   Plazo vencido; la operación atendió la cancelación");
}

Console.WriteLine("4. Circuit breaker: abrir, rechazar y recuperar");
var circuit = new CircuitBreaker(new CircuitBreakerOptions
{
    FailureThreshold = 1,
    BreakDuration = TimeSpan.FromMilliseconds(100),
    Telemetry = telemetry,
    OnStateChanged = e => Console.WriteLine($"   [evento {e.Sequence}] {e.PreviousState} → {e.CurrentState}"),
    ShouldHandle = ex => ex is HttpRequestException
});
try { await circuit.ExecuteAsync<int>(_ => Task.FromException<int>(new HttpRequestException())); }
catch (HttpRequestException) { Console.WriteLine($"   Estado: {circuit.State}"); }
try { await circuit.ExecuteAsync(_ => Task.FromResult(1)); }
catch (BrokenCircuitException) { Console.WriteLine("   Llamada rechazada sin ejecutar la operación"); }
await Task.Delay(150);
Console.WriteLine($"   Estado antes de prueba: {circuit.State}");
await circuit.ExecuteAsync(_ => Task.FromResult(1));
Console.WriteLine($"   Estado después de prueba: {circuit.State}");

Console.WriteLine("5. HTTP simulado: 503 y después 200 (sin red)");
using var client = new HttpClient(new DemoHandler());
var resilientClient = new ResilientHttpClient(client, new HttpRetryOptions
{
    BaseDelay = TimeSpan.Zero,
    MaxDelay = TimeSpan.Zero,
    Telemetry = telemetry
});
using var response = await resilientClient.SendAsync(
    () => new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/demo"));
Console.WriteLine($"   Respuesta final: {(int)response.StatusCode}");

Console.WriteLine("6. Composición: circuit breaker → retry → timeout por intento");
var composedCircuit = new CircuitBreaker(new CircuitBreakerOptions
{
    ShouldHandle = ex => ex is HttpRequestException or TimeoutRejectedException
});
var composedAttempts = 0;
var composed = await composedCircuit.ExecuteAsync(ct => Retry.ExecuteAsync(
    retryToken => ResilienceTimeout.ExecuteAsync(_ =>
    {
        composedAttempts++;
        return composedAttempts == 1
            ? Task.FromException<string>(new HttpRequestException())
            : Task.FromResult("Resultado compuesto");
    }, TimeSpan.FromSeconds(1), retryToken),
    new RetryOptions
    {
        BaseDelay = TimeSpan.Zero,
        ShouldRetry = ex => ex is HttpRequestException or TimeoutRejectedException
    }, ct));
Console.WriteLine($"   {composed}; intentos: {composedAttempts}; circuito: {composedCircuit.State}");

Console.WriteLine("7. Fallback: valor alternativo tras fallo seleccionado");
var cached = await Fallback.ExecuteAsync<string>(_ => throw new HttpRequestException(),
    (_, _) => Task.FromResult("Respuesta desde caché"),
    new FallbackOptions<string> { ShouldHandleException = ex => ex is HttpRequestException, Telemetry = telemetry });
Console.WriteLine($"   {cached}");

Console.WriteLine("8. Retry por resultado: -1, -1 y 42");
var resultAttempts = 0;
var accepted = await Retry.ExecuteAsync(_ => Task.FromResult(++resultAttempts < 3 ? -1 : 42),
    new RetryOptions<int> { BaseDelay = TimeSpan.Zero, ShouldRetryResult = value => value < 0, Telemetry = telemetry });
Console.WriteLine($"   Resultado {accepted} tras {resultAttempts} intentos");

Console.WriteLine("9. Rate limiting: un token y rechazo del siguiente envío");
using var tokenBucket = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
{
    TokenLimit = 1, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromMinutes(1),
    AutoReplenishment = false, QueueLimit = 0
});
var rateLimit = new RateLimit(tokenBucket, telemetry);
await rateLimit.ExecuteAsync(_ => Task.FromResult(1));
try { await rateLimit.ExecuteAsync(_ => Task.FromResult(2)); }
catch (RateLimitRejectedException error) { Console.WriteLine($"   Límite alcanzado; RetryAfter: {error.RetryAfter}"); }

Console.WriteLine("10. Hedging: alternativa rápida y cancelación del intento lento");
var fastest = await Hedging.ExecuteAsync(async (attempt, ct) =>
{
    if (attempt == 0) await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
    return "Resultado de la alternativa";
}, new HedgingOptions<string> { Delay = TimeSpan.FromMilliseconds(5), Telemetry = telemetry });
Console.WriteLine($"   {fastest}");

Console.WriteLine("11. IHttpClientFactory: cliente tipado y HTTP simulado");
var services = new ServiceCollection();
services.AddResilientHttpClient(client => client.BaseAddress = new Uri("https://example.invalid/"),
    _ => new HttpRetryOptions { BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, Telemetry = telemetry })
    .ConfigurePrimaryHttpMessageHandler(() => new DemoHandler());
using var provider = services.BuildServiceProvider();
using var factoryClient = provider.GetRequiredService<ResilientHttpClient>();
using var factoryResponse = await factoryClient.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "demo"));
Console.WriteLine($"   Respuesta del cliente tipado: {(int)factoryResponse.StatusCode}");

Console.WriteLine("12. Pipeline reutilizable por nombre en DI");
var pipelineServices = new ServiceCollection();
pipelineServices.AddResiliencePipeline<string>("catalogo", pipeline => pipeline
    .WithTelemetry(telemetry)
    .AddFallback((_, _) => Task.FromResult("Dato de caché"),
        new FallbackOptions<string> { ShouldHandleException = ex => ex is HttpRequestException or BrokenCircuitException, Telemetry = telemetry })
    .AddCircuitBreaker(new CircuitBreakerOptions
    {
        FailureThreshold = 2, BreakDuration = TimeSpan.FromSeconds(30),
        ShouldHandle = ex => ex is HttpRequestException, Telemetry = telemetry
    })
    .AddRetry(new RetryOptions { MaxRetries = 1, BaseDelay = TimeSpan.Zero, ShouldRetry = ex => ex is HttpRequestException, Telemetry = telemetry })
    .AddTimeout(TimeSpan.FromSeconds(2)));
using var pipelineProvider = pipelineServices.BuildServiceProvider();
var catalogPipeline = pipelineProvider.GetRequiredKeyedService<ResiliencePipeline<string>>("catalogo");
var pipelineAttempts = 0;
var pipelineResult = await catalogPipeline.ExecuteAsync(_ => ++pipelineAttempts == 1
    ? Task.FromException<string>(new HttpRequestException()) : Task.FromResult("Catálogo recuperado"));
Console.WriteLine($"   {pipelineResult}; intentos: {pipelineAttempts}");

internal sealed class DemoHandler : HttpMessageHandler
{
    private int _calls;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = ++_calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
        Console.WriteLine($"   HTTP intento {_calls}: {(int)status}");
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("Demo local") });
    }
}
