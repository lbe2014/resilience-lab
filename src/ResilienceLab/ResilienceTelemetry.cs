using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace ResilienceLab;

/// <summary>Optional structured logging and metrics for a stable dependency name.</summary>
/// <remarks>Observer failures are isolated from resilience behavior. No exception messages or request data are recorded.</remarks>
public sealed class ResilienceTelemetry
{
    public const string MeterName = "ResilienceLab";
    public const string ActivitySourceName = "ResilienceLab";
    private static readonly Meter Meter = new(MeterName, "0.5.0");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("resilience.retry.count", "{retry}");
    private static readonly Histogram<double> RetryDelay = Meter.CreateHistogram<double>("resilience.retry.delay", "s");
    private static readonly Counter<long> Timeouts = Meter.CreateCounter<long>("resilience.timeout.count", "{timeout}");
    private static readonly Counter<long> Transitions = Meter.CreateCounter<long>("resilience.circuit.transition.count", "{transition}");
    private static readonly Counter<long> Rejections = Meter.CreateCounter<long>("resilience.circuit.rejection.count", "{rejection}");
    private static readonly Counter<long> Fallbacks = Meter.CreateCounter<long>("resilience.fallback.count", "{fallback}");
    private static readonly Counter<long> HedgeAttempts = Meter.CreateCounter<long>("resilience.hedging.attempt.count", "{attempt}");
    private static readonly Counter<long> HedgeWins = Meter.CreateCounter<long>("resilience.hedging.win.count", "{win}");
    private static readonly Counter<long> RateAcquisitions = Meter.CreateCounter<long>("resilience.rate_limit.acquisition.count", "{acquisition}");
    private static readonly Histogram<double> RateWait = Meter.CreateHistogram<double>("resilience.rate_limit.wait", "s");
    private readonly ILogger? _logger;

    public string DependencyName { get; }

    public ResilienceTelemetry(string dependencyName, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dependencyName);
        if (dependencyName.Length > 128)
            throw new ArgumentOutOfRangeException(nameof(dependencyName), "Use a short, stable dependency name.");
        DependencyName = dependencyName;
        _logger = logger;
    }

    internal void RetryScheduled(string strategy, int retryNumber, TimeSpan delay,
        Exception? exception = null, int? statusCode = null)
    {
        var tags = Tags(strategy);
        ResilienceTracing.Event(this, "retry.scheduled", ("resilience.retry.number", retryNumber),
            ("resilience.retry.delay", delay.TotalSeconds), ("error.type", exception?.GetType().FullName),
            ("http.response.status_code", statusCode));
        if (exception is not null) tags.Add("error.type", exception.GetType().FullName);
        if (statusCode is not null) tags.Add("http.response.status_code", statusCode.Value);
        Observe(() => Retries.Add(1, tags));
        Observe(() => RetryDelay.Record(delay.TotalSeconds, tags));
        Observe(() => _logger?.LogWarning(new EventId(1001, "RetryScheduled"),
            "Dependency {DependencyName} strategy {Strategy} scheduled retry {RetryNumber} after {DelaySeconds} seconds; error type {ErrorType}; HTTP status {StatusCode}",
            DependencyName, strategy, retryNumber, delay.TotalSeconds, exception?.GetType().FullName, statusCode));
    }

    internal void TimeoutRejected(TimeSpan timeout)
    {
        ResilienceTracing.Event(this, "timeout.rejected", ("resilience.timeout", timeout.TotalSeconds));
        Observe(() => Timeouts.Add(1, Tags("timeout")));
        Observe(() => _logger?.LogWarning(new EventId(1002, "TimeoutRejected"),
            "Dependency {DependencyName} exceeded cooperative timeout {TimeoutSeconds} seconds",
            DependencyName, timeout.TotalSeconds));
    }

    internal void CircuitChanged(CircuitStateChangedEvent transition)
    {
        ResilienceTracing.Event(this, "circuit.changed", ("circuit.from", transition.PreviousState.ToString()),
            ("circuit.to", transition.CurrentState.ToString()), ("circuit.sequence", transition.Sequence));
        var tags = Tags("circuit_breaker");
        tags.Add("circuit.from", transition.PreviousState.ToString());
        tags.Add("circuit.to", transition.CurrentState.ToString());
        Observe(() => Transitions.Add(1, tags));
        Observe(() => _logger?.LogInformation(new EventId(1003, "CircuitStateChanged"),
            "Dependency {DependencyName} circuit changed from {PreviousState} to {CurrentState}; sequence {Sequence}",
            DependencyName, transition.PreviousState, transition.CurrentState, transition.Sequence));
    }

    internal void CircuitRejected()
    {
        ResilienceTracing.Event(this, "circuit.rejected");
        Observe(() => Rejections.Add(1, Tags("circuit_breaker")));
        Observe(() => _logger?.LogWarning(new EventId(1004, "CircuitRejected"),
            "Dependency {DependencyName} circuit rejected an operation", DependencyName));
    }

    private TagList Tags(string strategy) => new()
    {
        { "dependency.name", DependencyName },
        { "resilience.strategy", strategy }
    };

    internal void FallbackActivated(Exception? error)
    {
        ResilienceTracing.Event(this, "fallback.activated", ("error.type", error?.GetType().FullName));
        Observe(() => Fallbacks.Add(1, Tags("fallback")));
        Observe(() => _logger?.LogWarning(new EventId(1005, "FallbackActivated"),
            "Dependency {DependencyName} activated fallback; error type {ErrorType}", DependencyName, error?.GetType().FullName));
    }

    internal void HedgeStarted(int attempt)
    {
        ResilienceTracing.Event(this, "hedging.started", ("resilience.attempt", attempt));
        Observe(() => HedgeAttempts.Add(1, Tags("hedging")));
        Observe(() => _logger?.LogInformation(new EventId(1006, "HedgeAttemptStarted"),
            "Dependency {DependencyName} started hedge attempt {AttemptNumber}", DependencyName, attempt));
    }

    internal void HedgeWon()
    {
        ResilienceTracing.Event(this, "hedging.won");
        Observe(() => HedgeWins.Add(1, Tags("hedging")));
        Observe(() => _logger?.LogInformation(new EventId(1007, "HedgeWon"),
            "Dependency {DependencyName} completed hedging with an accepted result", DependencyName));
    }

    internal void RateLimitAcquired(bool acquired, TimeSpan wait)
    {
        ResilienceTracing.Event(this, "rate_limit.acquisition", ("rate_limit.outcome", acquired ? "acquired" : "rejected"),
            ("rate_limit.wait", wait.TotalSeconds));
        var tags = Tags("rate_limit");
        tags.Add("rate_limit.outcome", acquired ? "acquired" : "rejected");
        Observe(() => RateAcquisitions.Add(1, tags));
        Observe(() => RateWait.Record(wait.TotalSeconds, tags));
        Observe(() => _logger?.Log(acquired ? LogLevel.Debug : LogLevel.Warning,
            new EventId(1008, "RateLimitAcquisition"),
            "Dependency {DependencyName} rate limit acquisition {Outcome} after {WaitSeconds} seconds",
            DependencyName, acquired ? "acquired" : "rejected", wait.TotalSeconds));
    }

    internal static void Observe(Action action)
    {
        try { action(); }
        catch (Exception) { /* Diagnostics must never replace the operation's result or error. */ }
    }
}

/// <summary>A committed circuit transition. Sequence is monotonic per circuit instance.</summary>
public sealed record CircuitStateChangedEvent(
    long Sequence, CircuitState PreviousState, CircuitState CurrentState, DateTimeOffset Timestamp);
