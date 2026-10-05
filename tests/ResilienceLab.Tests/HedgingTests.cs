using Microsoft.Extensions.Time.Testing;

namespace ResilienceLab.Tests;

public class HedgingTests
{
    private static HedgingOptions<int> Options() => new() { Delay = TimeSpan.FromSeconds(1) };
    private static ResilienceRuntime Runtime(FakeTimeProvider clock) => new(clock, () => 1);

    [Fact]
    public async Task ImmediatePrimarySuccessPreventsDelayedAlternatives()
    {
        var attempts = new List<int>();
        Assert.Equal(42, await Hedging.ExecuteAsync((index, _) =>
        { attempts.Add(index); return Task.FromResult(42); }, Options(), Runtime(new FakeTimeProvider())));
        Assert.Equal(new[] { 0 }, attempts);
    }

    [Fact]
    public async Task StartsAlternativeOnlyAtConfiguredDelayAndCancelsPrimary()
    {
        var clock = new FakeTimeProvider(); var attempts = new List<int>();
        CancellationToken primaryToken = default;
        var task = Hedging.ExecuteAsync(async (index, ct) =>
        {
            attempts.Add(index);
            if (index == 0) { primaryToken = ct; await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct); }
            return 42;
        }, Options(), Runtime(clock));
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(new[] { 0 }, attempts);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(42, await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(primaryToken.IsCancellationRequested);
        Assert.Equal(new[] { 0, 1 }, attempts);
    }

    [Fact]
    public async Task FailedAlternativeDoesNotWinAndPrimaryCanRecover()
    {
        var clock = new FakeTimeProvider();
        var primary = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var alternativeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Hedging.ExecuteAsync((index, _) =>
        {
            if (index == 0) return primary.Task;
            alternativeStarted.SetResult();
            return Task.FromException<int>(new HttpRequestException());
        }, Options(), Runtime(clock));
        clock.Advance(TimeSpan.FromSeconds(1)); await alternativeStarted.Task;
        Assert.False(task.IsCompleted);
        primary.SetResult(42);
        Assert.Equal(42, await task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AllFaultedAttemptsProduceAggregateError()
    {
        var attempts = 0;
        var error = await Assert.ThrowsAsync<HedgingRejectedException>(() => Hedging.ExecuteAsync<int>((_, _) =>
        { attempts++; throw new HttpRequestException(); }, Options() with { Delay = TimeSpan.Zero }));
        Assert.Equal(2, attempts);
        Assert.Equal(2, Assert.IsType<AggregateException>(error.InnerException).InnerExceptions.Count);
    }

    [Fact]
    public async Task RejectsAndDisposesUnacceptableResults()
    {
        var results = new[] { new ResultStrategiesTests.Resource(), new ResultStrategiesTests.Resource() };
        await Assert.ThrowsAsync<HedgingRejectedException>(() => Hedging.ExecuteAsync((index, _) => Task.FromResult(results[index]),
            new HedgingOptions<ResultStrategiesTests.Resource> { Delay = TimeSpan.Zero, ShouldAcceptResult = _ => false }));
        Assert.All(results, result => Assert.Equal(1, result.Disposals));
    }

    [Fact]
    public async Task WaitsForIgnoringLoserAndDisposesItsLateResult()
    {
        var loser = new ResultStrategiesTests.Resource(); var winner = new ResultStrategiesTests.Resource();
        var late = new TaskCompletionSource<ResultStrategiesTests.Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        var task = Hedging.ExecuteAsync((index, ct) =>
        {
            if (index == 0) { registration = ct.Register(() => canceled.TrySetResult()); return late.Task; }
            return Task.FromResult(winner);
        }, new HedgingOptions<ResultStrategiesTests.Resource> { Delay = TimeSpan.Zero });
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(task.IsCompleted);
        late.SetResult(loser);
        Assert.Same(winner, await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, loser.Disposals); Assert.Equal(0, winner.Disposals);
        registration.Dispose();
    }

    [Fact]
    public async Task ConsumerCancellationDrainsPendingAttemptsAndDisposesLateResult()
    {
        using var source = new CancellationTokenSource();
        var result = new ResultStrategiesTests.Resource();
        var completion = new TaskCompletionSource<ResultStrategiesTests.Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Hedging.ExecuteAsync((_, _) => completion.Task,
            new HedgingOptions<ResultStrategiesTests.Resource>(), source.Token);
        source.Cancel(); completion.SetResult(result);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, result.Disposals);
    }

    [Fact]
    public async Task ClassifierFailureDisposesResultAndPropagatesOriginalError()
    {
        var result = new ResultStrategiesTests.Resource(); var error = new InvalidOperationException();
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => Hedging.ExecuteAsync((_, _) => Task.FromResult(result),
            new HedgingOptions<ResultStrategiesTests.Resource> { ShouldAcceptResult = _ => throw error })));
        Assert.Equal(1, result.Disposals);
    }

    [Fact]
    public async Task CleanupErrorsReleaseWinnerAndAreReported()
    {
        var winner = new ResultStrategiesTests.Resource(); var loser = new ResultStrategiesTests.Resource();
        await Assert.ThrowsAsync<AggregateException>(() => Hedging.ExecuteAsync((index, _) => Task.FromResult(index == 0 ? winner : loser),
            new HedgingOptions<ResultStrategiesTests.Resource>
            {
                Delay = TimeSpan.Zero,
                OnDiscardResult = result => { result.Dispose(); if (ReferenceEquals(result, loser)) throw new InvalidOperationException(); return ValueTask.CompletedTask; }
            }));
        Assert.Equal(1, winner.Disposals); Assert.Equal(1, loser.Disposals);
    }

    [Theory] [InlineData(-1)] [InlineData(33)]
    public async Task ValidatesAttemptLimitBeforeInvokingOperation(int attempts)
    {
        var called = false;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Hedging.ExecuteAsync((_, _) =>
        { called = true; return Task.FromResult(1); }, Options() with { MaxHedgedAttempts = attempts }));
        Assert.False(called);
    }

    [Fact]
    public async Task ZeroAdditionalAttemptsRunsOnlyPrimary()
    {
        var attempts = 0;
        Assert.Equal(42, await Hedging.ExecuteAsync((_, _) => { attempts++; return Task.FromResult(42); }, Options() with { MaxHedgedAttempts = 0 }));
        Assert.Equal(1, attempts);
    }
}
