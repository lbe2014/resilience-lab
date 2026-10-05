using System.Runtime.ExceptionServices;

namespace ResilienceLab;

public sealed record HedgingOptions<T>
{
    /// <summary>Additional attempts beyond attempt zero; at most 32.</summary>
    public int MaxHedgedAttempts { get; init; } = 1;
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(200);
    public Func<T, bool> ShouldAcceptResult { get; init; } = _ => true;
    public Func<T, ValueTask>? OnDiscardResult { get; init; }
    public ResilienceTelemetry? Telemetry { get; init; }
}

public sealed class HedgingRejectedException(IReadOnlyList<Exception> errors)
    : Exception("No hedging attempt produced an accepted result.", errors.Count == 0 ? null : new AggregateException(errors));

public static class Hedging
{
    /// <summary>Starts staggered alternatives, selects a winner, cancels and drains all other attempts.</summary>
    public static Task<T> ExecuteAsync<T>(Func<int, CancellationToken, Task<T>> operation,
        HedgingOptions<T> options, CancellationToken cancellationToken = default) =>
        ExecuteAsync(operation, options, ResilienceRuntime.System, cancellationToken);

    internal static Task<T> ExecuteAsync<T>(Func<int, CancellationToken, Task<T>> operation,
        HedgingOptions<T> options, ResilienceRuntime runtime, CancellationToken cancellationToken = default) =>
        ResilienceTracing.ExecuteAsync(options?.Telemetry, "hedging", () => ExecuteCoreAsync(operation, options!, runtime, cancellationToken));

    private static async Task<T> ExecuteCoreAsync<T>(Func<int, CancellationToken, Task<T>> operation,
        HedgingOptions<T> options, ResilienceRuntime runtime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ShouldAcceptResult);
        if (options.MaxHedgedAttempts < 0 || options.MaxHedgedAttempts > 32)
            throw new ArgumentOutOfRangeException(nameof(options.MaxHedgedAttempts));
        DelayValidation.Validate(options.Delay, nameof(options.Delay));
        if (options.Delay.Ticks > 0 && options.MaxHedgedAttempts > 0)
            DelayValidation.Validate(TimeSpan.FromTicks(options.Delay.Ticks * options.MaxHedgedAttempts), nameof(options.Delay));
        cancellationToken.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task<ResilienceOutcome<T>>>();
        var errors = new List<Exception>();
        var cleanupErrors = new List<Exception>();
        var hasWinner = false;
        T winner = default!;
        Exception? terminalError = null;

        async Task<ResilienceOutcome<T>> Run(int attempt)
        {
            try
            {
                if (attempt > 0)
                    await runtime.DelayAsync(TimeSpan.FromTicks(options.Delay.Ticks * attempt), linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                options.Telemetry?.HedgeStarted(attempt);
                return ResilienceOutcome<T>.FromResult(await ResilienceTracing.ExecuteAsync(options.Telemetry,
                    "hedging.attempt", () => operation(attempt, linked.Token), attempt).ConfigureAwait(false));
            }
            catch (Exception error) { return ResilienceOutcome<T>.FromException(error); }
        }

        // All delays are relative to launch; cancellation prevents pending alternatives from invoking the operation.
        pending.Add(Run(0));
        for (var attempt = 1; attempt <= options.MaxHedgedAttempts; attempt++) pending.Add(Run(attempt));
        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                var outcome = await completed.ConfigureAwait(false);
                if (outcome.HasResult)
                {
                    var keep = false;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (options.ShouldAcceptResult(outcome.Result!))
                        {
                            winner = outcome.Result!;
                            hasWinner = keep = true;
                            break;
                        }
                    }
                    finally
                    {
                        if (!keep) await ResultCleanup.DiscardAsync(outcome.Result!, options.OnDiscardResult).ConfigureAwait(false);
                    }
                }
                else if (outcome.Exception is { } error) errors.Add(error);

            }
        }
        catch (Exception error) { terminalError = error; }
        finally
        {
            try { await linked.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { cleanupErrors.Add(error); }
            foreach (var task in pending)
            {
                var outcome = await task.ConfigureAwait(false);
                if (outcome.HasResult)
                {
                    try { await ResultCleanup.DiscardAsync(outcome.Result!, options.OnDiscardResult).ConfigureAwait(false); }
                    catch (Exception error) { cleanupErrors.Add(error); }
                }
            }
        }

        if (hasWinner && (terminalError is not null || cleanupErrors.Count > 0 || cancellationToken.IsCancellationRequested))
        {
            try { await ResultCleanup.DiscardAsync(winner, options.OnDiscardResult).ConfigureAwait(false); }
            catch (Exception error) { cleanupErrors.Add(error); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (cleanupErrors.Count > 0)
        {
            if (terminalError is not null) cleanupErrors.Insert(0, terminalError);
            throw new AggregateException("Hedging cleanup failed.", cleanupErrors);
        }
        if (terminalError is not null) ExceptionDispatchInfo.Capture(terminalError).Throw();
        if (!hasWinner) throw new HedgingRejectedException(errors);
        options.Telemetry?.HedgeWon();
        return winner;
    }
}
