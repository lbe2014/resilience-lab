using System.Threading.RateLimiting;

namespace ResilienceLab;

/// <summary>A reusable composition. Registrations run from outermost to innermost.</summary>
public sealed class ResiliencePipeline<T>
{
    private readonly Func<Func<CancellationToken, Task<T>>, CancellationToken, Task<T>> _execute;
    private readonly ResilienceTelemetry? _telemetry;

    internal ResiliencePipeline(Func<Func<CancellationToken, Task<T>>, CancellationToken, Task<T>> execute,
        ResilienceTelemetry? telemetry = null) => (_execute, _telemetry) = (execute, telemetry);

    public Task<T> ExecuteAsync(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        return ResilienceTracing.ExecuteAsync(_telemetry, "pipeline", () => _execute(operation, cancellationToken));
    }
}

/// <summary>Builds independent pipelines; a built pipeline is unaffected by later registrations.</summary>
public sealed class ResiliencePipelineBuilder<T>
{
    private delegate Task<T> Strategy(Func<CancellationToken, Task<T>> operation, CancellationToken token);
    private readonly List<Func<Strategy>> _strategies = [];
    private readonly ResilienceRuntime _runtime;
    private ResilienceTelemetry? _telemetry;

    /// <summary>Sets pipeline telemetry and the default for strategies without explicit telemetry.</summary>
    public ResiliencePipelineBuilder<T> WithTelemetry(ResilienceTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        _telemetry = telemetry;
        return this;
    }

    public ResiliencePipelineBuilder() : this(ResilienceRuntime.System) { }
    internal ResiliencePipelineBuilder(ResilienceRuntime runtime) => _runtime = runtime;

    public ResiliencePipelineBuilder<T> AddRetry(RetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _strategies.Add(() =>
        {
            var configured = options with { Telemetry = options.Telemetry ?? _telemetry };
            return (operation, token) => Retry.ExecuteAsync(operation, configured, _runtime, token);
        });
        return this;
    }

    public ResiliencePipelineBuilder<T> AddRetry(RetryOptions<T> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _strategies.Add(() =>
        {
            var configured = options with { Telemetry = options.Telemetry ?? _telemetry };
            return (operation, token) => Retry.ExecuteAsync(operation, configured, _runtime, token);
        });
        return this;
    }

    public ResiliencePipelineBuilder<T> AddTimeout(TimeSpan duration) =>
        AddTimeout(new TimeoutOptions { TimeoutDuration = duration });

    public ResiliencePipelineBuilder<T> AddTimeout(TimeoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _strategies.Add(() =>
        {
            var configured = options with { Telemetry = options.Telemetry ?? _telemetry };
            return (operation, token) => Timeout.ExecuteAsync(operation, configured, _runtime.TimeProvider, token);
        });
        return this;
    }

    public ResiliencePipelineBuilder<T> AddCircuitBreaker(CircuitBreakerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _strategies.Add(() =>
        {
            var circuit = new CircuitBreaker(options with { Telemetry = options.Telemetry ?? _telemetry }, _runtime.TimeProvider);
            return (operation, token) => circuit.ExecuteAsync(operation, token);
        });
        return this;
    }

    public ResiliencePipelineBuilder<T> AddFallback(
        Func<ResilienceOutcome<T>, CancellationToken, Task<T>> fallback, FallbackOptions<T> options)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(options);
        _strategies.Add(() =>
        {
            var configured = options with { Telemetry = options.Telemetry ?? _telemetry };
            return (operation, token) => Fallback.ExecuteAsync(operation, fallback, configured, token);
        });
        return this;
    }

    /// <summary>The consumer owns the limiter; sharing it shares its budget across pipelines.</summary>
    public ResiliencePipelineBuilder<T> AddRateLimit(RateLimiter limiter, ResilienceTelemetry? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        _strategies.Add(() =>
        {
            var rateLimit = new RateLimit(limiter, telemetry ?? _telemetry);
            return (operation, token) => rateLimit.ExecuteAsync(operation, token);
        });
        return this;
    }

    /// <summary>May run the same operation concurrently; use only for idempotent operations.</summary>
    public ResiliencePipelineBuilder<T> AddHedging(HedgingOptions<T> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _strategies.Add(() =>
        {
            var configured = options with { Telemetry = options.Telemetry ?? _telemetry };
            return (operation, token) => Hedging.ExecuteAsync((_, ct) => operation(ct), configured, _runtime, token);
        });
        return this;
    }

    public ResiliencePipeline<T> Build()
    {
        var strategies = _strategies.Select(create => create()).ToArray();
        Func<Func<CancellationToken, Task<T>>, CancellationToken, Task<T>> execute = (operation, token) => operation(token);
        for (var index = strategies.Length - 1; index >= 0; index--)
        {
            var strategy = strategies[index];
            var next = execute;
            execute = (operation, token) => strategy(ct => next(operation, ct), token);
        }
        return new ResiliencePipeline<T>(execute, _telemetry);
    }
}
