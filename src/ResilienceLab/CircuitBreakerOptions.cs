namespace ResilienceLab;

public sealed record CircuitBreakerOptions
{
    public int FailureThreshold { get; init; } = 5;
    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(30);
    public ResilienceTelemetry? Telemetry { get; init; }

    /// <summary>Invoked outside the circuit lock. Observer exceptions are ignored.</summary>
    public Action<CircuitStateChangedEvent>? OnStateChanged { get; init; }

    /// <summary>
    /// Selects failures that count toward opening the circuit. Cancellation never counts.
    /// If this predicate throws, the original operation exception is preserved.
    /// </summary>
    public required Func<Exception, bool> ShouldHandle { get; init; }
}
