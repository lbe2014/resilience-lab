namespace ResilienceLab;

/// <summary>Configuration for exception-based retries. MaxRetries excludes the initial attempt.</summary>
public sealed record RetryOptions
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);
    public bool UseJitter { get; init; } = true;
    public required Func<Exception, bool> ShouldRetry { get; init; }
    public Action<RetryEvent>? OnRetry { get; init; }
    public ResilienceTelemetry? Telemetry { get; init; }
}

/// <summary>A retry scheduled after an unsuccessful operation, numbered from one.</summary>
public sealed record RetryEvent(int RetryNumber, Exception Exception, TimeSpan Delay);

public static partial class Retry
{
    public static Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryOptions options,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(operation, options, ResilienceRuntime.System, cancellationToken);

    internal static Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, RetryOptions options,
        ResilienceRuntime runtime, CancellationToken cancellationToken = default) =>
        ResilienceTracing.ExecuteAsync(options?.Telemetry, "retry", () => ExecuteCoreAsync(operation, options!, runtime, cancellationToken));

    private static async Task<T> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryOptions options,
        ResilienceRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ShouldRetry);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxRetries);
        DelayValidation.Validate(options.BaseDelay, nameof(options.BaseDelay));
        DelayValidation.Validate(options.MaxDelay, nameof(options.MaxDelay));
        if (options.MaxDelay < options.BaseDelay)
            throw new ArgumentOutOfRangeException(nameof(options.MaxDelay));

        var retries = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await ResilienceTracing.ExecuteAsync(options.Telemetry, "retry.attempt",
                    () => operation(cancellationToken), retries).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException
                && !cancellationToken.IsCancellationRequested
                && retries < options.MaxRetries && options.ShouldRetry(error))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var delay = RetryDelay.Calculate(options.BaseDelay, options.MaxDelay,
                    retries, options.UseJitter ? runtime.NextDouble() : 1);
                retries++;
                options.OnRetry?.Invoke(new RetryEvent(retries, error, delay));
                cancellationToken.ThrowIfCancellationRequested();
                options.Telemetry?.RetryScheduled("retry", retries, delay, error);
                await runtime.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
