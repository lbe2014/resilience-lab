namespace ResilienceLab;

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen
}

/// <summary>A thread-safe circuit shared across calls to the same dependency.</summary>
public sealed class CircuitBreaker
{
    private readonly object _gate = new();
    private readonly CircuitBreakerOptions _options;
    private readonly TimeProvider _timeProvider;
    private CircuitState _state;
    private long _generation;
    private long _openedAt;
    private int _consecutiveFailures;
    private bool _probeRunning;
    private long _transitionSequence;

    public CircuitBreaker(CircuitBreakerOptions options) : this(options, TimeProvider.System)
    {
    }

    internal CircuitBreaker(CircuitBreakerOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ShouldHandle);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.FailureThreshold);
        if (options.BreakDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.BreakDuration));

        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>Reports HalfOpen once the break duration has elapsed; no background timer is used.</summary>
    public CircuitState State
    {
        get
        {
            CircuitStateChangedEvent? transition = null;
            CircuitState state;
            lock (_gate)
            {
                transition = RefreshOpenState();
                state = _state;
            }
            Notify(transition);
            return state;
        }
    }

    public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) =>
        ResilienceTracing.ExecuteAsync(_options.Telemetry, "circuit_breaker", () => ExecuteCoreAsync(operation, cancellationToken));

    private async Task<T> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        var generation = Admit(cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await operation(cancellationToken).ConfigureAwait(false);
            Complete(generation, succeeded: true, handledFailure: false);
            return result;
        }
        catch (Exception error)
        {
            var handled = false;
            if (error is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // User code must execute outside the state lock.
                    handled = _options.ShouldHandle(error);
                }
                catch
                {
                    // A broken filter must not replace the dependency's exception or strand a probe.
                }
            }

            Complete(generation, succeeded: false,
                handledFailure: handled && !cancellationToken.IsCancellationRequested);
            throw;
        }
    }

    private long Admit(CancellationToken cancellationToken)
    {
        CircuitStateChangedEvent? transition = null;
        try
        {
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                transition = RefreshOpenState();
                if (_state == CircuitState.Open || (_state == CircuitState.HalfOpen && _probeRunning))
                    throw new BrokenCircuitException();

                if (_state == CircuitState.HalfOpen)
                    _probeRunning = true;

                return _generation;
            }
        }
        catch (BrokenCircuitException)
        {
            _options.Telemetry?.CircuitRejected();
            throw;
        }
        finally { Notify(transition); }
    }

    // Called only with _gate held. Timestamp-based elapsed time is immune to wall-clock corrections.
    private CircuitStateChangedEvent? RefreshOpenState()
    {
        if (_state == CircuitState.Open &&
            _timeProvider.GetElapsedTime(_openedAt) >= _options.BreakDuration)
        {
            var transition = TransitionTo(CircuitState.HalfOpen);
            _generation++;
            _probeRunning = false;
            return transition;
        }
        return null;
    }

    private void Complete(long generation, bool succeeded, bool handledFailure)
    {
        CircuitStateChangedEvent? transition = null;
        lock (_gate)
        {
            // Calls admitted before a state transition must not affect the new state.
            if (generation != _generation)
                return;

            if (_state == CircuitState.HalfOpen)
            {
                _probeRunning = false;
                if (succeeded)
                {
                    transition = TransitionTo(CircuitState.Closed);
                    _generation++;
                    _consecutiveFailures = 0;
                }
                else if (handledFailure)
                {
                    transition = Open();
                }
            }
            else if (_state == CircuitState.Closed)
            {
                if (succeeded)
                    _consecutiveFailures = 0;
                else if (handledFailure && ++_consecutiveFailures >= _options.FailureThreshold)
                    transition = Open();
            }
        }
        Notify(transition);
    }

    private CircuitStateChangedEvent Open()
    {
        var transition = TransitionTo(CircuitState.Open);
        _generation++;
        _openedAt = _timeProvider.GetTimestamp();
        _probeRunning = false;
        return transition;
    }

    // Capture committed transitions under the lock; all observers run after releasing it.
    private CircuitStateChangedEvent TransitionTo(CircuitState state)
    {
        var previous = _state;
        _state = state;
        return new CircuitStateChangedEvent(++_transitionSequence, previous, state, _timeProvider.GetUtcNow());
    }

    private void Notify(CircuitStateChangedEvent? transition)
    {
        if (transition is null) return;
        _options.Telemetry?.CircuitChanged(transition);
        if (_options.OnStateChanged is { } callback)
            ResilienceTelemetry.Observe(() => callback(transition));
    }
}
