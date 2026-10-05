using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ResilienceLab.Tests")]
[assembly: InternalsVisibleTo("ResilienceLab.Http")]
[assembly: InternalsVisibleTo("ResilienceLab.Http.Tests")]

namespace ResilienceLab;

internal sealed record ResilienceRuntime(TimeProvider TimeProvider, Func<double> NextDouble)
{
    internal static ResilienceRuntime System { get; } = new(TimeProvider.System, Random.Shared.NextDouble);
    internal Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, TimeProvider, cancellationToken);
}

internal static class DelayValidation
{
    internal static void Validate(TimeSpan duration, string paramName, bool allowZero = true)
    {
        if (duration < TimeSpan.Zero || (!allowZero && duration == TimeSpan.Zero)
            || duration.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(paramName, "Duration is outside the supported timer range.");
    }
}

internal static class RetryDelay
{
    internal static TimeSpan Calculate(TimeSpan baseDelay, TimeSpan maxDelay, int retryIndex, double sample)
    {
        // Handle zero separately: 0 * infinity would yield NaN at large indices.
        var milliseconds = baseDelay == TimeSpan.Zero ? 0 :
            Math.Min(maxDelay.TotalMilliseconds, baseDelay.TotalMilliseconds * Math.Pow(2, retryIndex));
        return TimeSpan.FromMilliseconds(milliseconds * sample);
    }
}
