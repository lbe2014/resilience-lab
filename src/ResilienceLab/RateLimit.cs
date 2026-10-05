using System.Threading.RateLimiting;

namespace ResilienceLab;

public sealed class RateLimitRejectedException(TimeSpan? retryAfter)
    : Exception("The rate limiter rejected the operation.")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Applies a shared .NET rate limiter. The caller owns and disposes the limiter.</summary>
public sealed class RateLimit(RateLimiter limiter, ResilienceTelemetry? telemetry = null)
{
    private readonly RateLimiter _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));

    public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) =>
        ResilienceTracing.ExecuteAsync(telemetry, "rate_limit", () => ExecuteCoreAsync(operation, cancellationToken));

    private async Task<T> ExecuteCoreAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var lease = await _limiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        telemetry?.RateLimitAcquired(lease.IsAcquired, System.Diagnostics.Stopwatch.GetElapsedTime(started));
        if (!lease.IsAcquired)
        {
            TimeSpan? retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var delay) ? delay : null;
            throw new RateLimitRejectedException(retryAfter);
        }
        return await operation(cancellationToken).ConfigureAwait(false);
    }
}
