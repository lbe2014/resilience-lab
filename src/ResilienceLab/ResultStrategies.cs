namespace ResilienceLab;

/// <summary>A result or error passed to a resilience callback.</summary>
public readonly record struct ResilienceOutcome<T>
{
    public bool HasResult { get; }
    public T? Result { get; }
    public Exception? Exception { get; }

    private ResilienceOutcome(bool hasResult, T? result, Exception? exception) =>
        (HasResult, Result, Exception) = (hasResult, result, exception);
    internal static ResilienceOutcome<T> FromResult(T result) => new(true, result, null);
    internal static ResilienceOutcome<T> FromException(Exception error) => new(false, default, error);
}

internal static class ResultCleanup
{
    internal static async ValueTask DiscardAsync<T>(T result, Func<T, ValueTask>? cleanup)
    {
        if (cleanup is not null) await cleanup(result).ConfigureAwait(false);
        else if (result is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (result is IDisposable disposable) disposable.Dispose();
    }
}

public sealed record RetryOptions<T>
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);
    public bool UseJitter { get; init; } = true;
    public required Func<T, bool> ShouldRetryResult { get; init; }
    public Func<Exception, bool>? ShouldRetryException { get; init; }
    public Func<T, ValueTask>? OnDiscardResult { get; init; }
    public Action<ResultRetryEvent<T>>? OnRetry { get; init; }
    public ResilienceTelemetry? Telemetry { get; init; }
}

public sealed record ResultRetryEvent<T>(int RetryNumber, ResilienceOutcome<T> Outcome, TimeSpan Delay);

public static partial class Retry
{
    public static Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation,
        RetryOptions<T> options, CancellationToken cancellationToken = default) =>
        ExecuteAsync(operation, options, ResilienceRuntime.System, cancellationToken);

    internal static Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation,
        RetryOptions<T> options, ResilienceRuntime runtime, CancellationToken cancellationToken = default) =>
        ResilienceTracing.ExecuteAsync(options?.Telemetry, "result_retry", () => ExecuteCoreAsync(operation, options!, runtime, cancellationToken));

    private static async Task<T> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation,
        RetryOptions<T> options, ResilienceRuntime runtime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ShouldRetryResult);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxRetries);
        DelayValidation.Validate(options.BaseDelay, nameof(options.BaseDelay));
        DelayValidation.Validate(options.MaxDelay, nameof(options.MaxDelay));
        if (options.MaxDelay < options.BaseDelay) throw new ArgumentOutOfRangeException(nameof(options.MaxDelay));
        var retries = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResilienceOutcome<T> outcome;
            try { outcome = ResilienceOutcome<T>.FromResult(await ResilienceTracing.ExecuteAsync(options.Telemetry,
                "result_retry.attempt", () => operation(cancellationToken), retries).ConfigureAwait(false)); }
            catch (Exception error) when (error is not OperationCanceledException
                && !cancellationToken.IsCancellationRequested && retries < options.MaxRetries
                && options.ShouldRetryException?.Invoke(error) == true)
            {
                outcome = ResilienceOutcome<T>.FromException(error);
            }

            if (outcome.HasResult && retries >= options.MaxRetries) return outcome.Result!;
            var keep = false;
            TimeSpan delay;
            try
            {
                if (outcome.HasResult && !options.ShouldRetryResult(outcome.Result!))
                {
                    keep = true;
                    return outcome.Result!;
                }
                cancellationToken.ThrowIfCancellationRequested();
                delay = RetryDelay.Calculate(options.BaseDelay, options.MaxDelay, retries,
                    options.UseJitter ? runtime.NextDouble() : 1);
                retries++;
                options.OnRetry?.Invoke(new(retries, outcome, delay));
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                if (outcome.HasResult && !keep)
                    await ResultCleanup.DiscardAsync(outcome.Result!, options.OnDiscardResult).ConfigureAwait(false);
            }
            options.Telemetry?.RetryScheduled("result_retry", retries, delay, outcome.Exception);
            await runtime.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed record FallbackOptions<T>
{
    public Func<Exception, bool>? ShouldHandleException { get; init; }
    public Func<T, bool>? ShouldHandleResult { get; init; }
    public Func<T, ValueTask>? OnDiscardResult { get; init; }
    public ResilienceTelemetry? Telemetry { get; init; }
}

public static class Fallback
{
    public static Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation,
        Func<ResilienceOutcome<T>, CancellationToken, Task<T>> fallback,
        FallbackOptions<T> options, CancellationToken cancellationToken = default) =>
        ResilienceTracing.ExecuteAsync(options?.Telemetry, "fallback", () => ExecuteCoreAsync(operation, fallback, options!, cancellationToken));

    private static async Task<T> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation,
        Func<ResilienceOutcome<T>, CancellationToken, Task<T>> fallback,
        FallbackOptions<T> options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ShouldHandleException is null && options.ShouldHandleResult is null)
            throw new ArgumentException("Select at least one failure condition.", nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        ResilienceOutcome<T> outcome;
        try { outcome = ResilienceOutcome<T>.FromResult(await operation(cancellationToken).ConfigureAwait(false)); }
        catch (Exception error) when (error is not OperationCanceledException
            && !cancellationToken.IsCancellationRequested && options.ShouldHandleException?.Invoke(error) == true)
        {
            outcome = ResilienceOutcome<T>.FromException(error);
        }
        var keep = false;
        var alternativeCreated = false;
        T alternative = default!;
        try
        {
            if (outcome.HasResult && options.ShouldHandleResult?.Invoke(outcome.Result!) != true)
            {
                keep = true;
                return outcome.Result!;
            }
            cancellationToken.ThrowIfCancellationRequested();
            options.Telemetry?.FallbackActivated(outcome.Exception);
            // The callback may inspect a rejected result while it is still alive.
            alternative = await ResilienceTracing.ExecuteAsync(options.Telemetry, "fallback.execute",
                () => fallback(outcome, cancellationToken)).ConfigureAwait(false);
            alternativeCreated = true;
            if (outcome.HasResult && ReferenceEquals(alternative, outcome.Result)) keep = true;
            return alternative;
        }
        finally
        {
            if (outcome.HasResult && !keep)
            {
                try { await ResultCleanup.DiscardAsync(outcome.Result!, options.OnDiscardResult).ConfigureAwait(false); }
                catch (Exception cleanupError)
                {
                    if (alternativeCreated)
                    {
                        try { await ResultCleanup.DiscardAsync(alternative, options.OnDiscardResult).ConfigureAwait(false); }
                        catch (Exception alternativeCleanupError) { throw new AggregateException(cleanupError, alternativeCleanupError); }
                    }
                    throw;
                }
            }
        }
    }
}
