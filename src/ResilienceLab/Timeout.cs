namespace ResilienceLab;

public sealed record TimeoutOptions
{
    public required TimeSpan TimeoutDuration { get; init; }
    public ResilienceTelemetry? Telemetry { get; init; }
}

public sealed class TimeoutRejectedException : TimeoutException
{
    public TimeSpan TimeoutDuration { get; }

    public TimeoutRejectedException(TimeSpan timeoutDuration, Exception? innerException = null)
        : base($"The operation exceeded its cooperative timeout of {timeoutDuration}.", innerException)
    {
        TimeoutDuration = timeoutDuration;
    }
}

/// <summary>Requests cancellation at the deadline and waits for the operation to settle.</summary>
/// <remarks>Late results are discarded. Dispose owned resources inside the operation and return a value or DTO.</remarks>
public static class Timeout
{
    public static Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout, CancellationToken cancellationToken = default) =>
        ExecuteAsync(operation, timeout, TimeProvider.System, cancellationToken);

    public static Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation,
        TimeoutOptions options, CancellationToken cancellationToken = default) =>
        ExecuteAsync(operation, options, TimeProvider.System, cancellationToken);

    internal static Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken = default) =>
        ExecuteAsync(operation, new TimeoutOptions { TimeoutDuration = timeout }, timeProvider, cancellationToken);

    internal static Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, TimeoutOptions options,
        TimeProvider timeProvider, CancellationToken cancellationToken = default) =>
        ResilienceTracing.ExecuteAsync(options?.Telemetry, "timeout", () => ExecuteCoreAsync(operation, options!, timeProvider, cancellationToken));

    private static async Task<T> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation,
        TimeoutOptions options, TimeProvider timeProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var timeout = options.TimeoutDuration;
        DelayValidation.Validate(timeout, nameof(timeout), allowZero: false);
        cancellationToken.ThrowIfCancellationRequested();

        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        T result;
        try
        {
            result = await operation(linked.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
            {
                options.Telemetry?.TimeoutRejected(timeout);
                throw new TimeoutRejectedException(timeout, error);
            }
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (deadline.IsCancellationRequested)
        {
            options.Telemetry?.TimeoutRejected(timeout);
            throw new TimeoutRejectedException(timeout);
        }
        return result;
    }
}
