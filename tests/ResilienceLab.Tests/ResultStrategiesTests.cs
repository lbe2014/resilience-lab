namespace ResilienceLab.Tests;

public class ResultStrategiesTests
{
    private static RetryOptions<int> Options(int maxRetries = 3) => new()
    {
        MaxRetries = maxRetries, BaseDelay = TimeSpan.Zero, ShouldRetryResult = value => value < 0
    };

    [Fact]
    public async Task RetryRecoversFromRejectedResultsAndRecordsOutcome()
    {
        var attempts = 0;
        var events = new List<ResultRetryEvent<int>>();
        Assert.Equal(42, await Retry.ExecuteAsync(_ => Task.FromResult(++attempts < 3 ? -1 : 42),
            Options() with { OnRetry = events.Add }));
        Assert.Equal(3, attempts);
        Assert.Equal(new[] { 1, 2 }, events.Select(e => e.RetryNumber));
        Assert.All(events, e => { Assert.True(e.Outcome.HasResult); Assert.Equal(-1, e.Outcome.Result); });
    }

    [Theory] [InlineData(0)] [InlineData(2)]
    public async Task RetryReturnsFinalRejectedResultWhenBudgetIsExhausted(int retries)
    {
        var attempts = 0;
        Assert.Equal(-1, await Retry.ExecuteAsync(_ => { attempts++; return Task.FromResult(-1); }, Options(retries)));
        Assert.Equal(retries + 1, attempts);
    }

    [Fact]
    public async Task RetryHandlesBothExceptionsAndResultsWhenConfigured()
    {
        var attempts = 0;
        var events = new List<ResultRetryEvent<int>>();
        Assert.Equal(42, await Retry.ExecuteAsync(_ => ++attempts switch
        {
            1 => Task.FromException<int>(new HttpRequestException()),
            2 => Task.FromResult(-1),
            _ => Task.FromResult(42)
        }, Options() with { ShouldRetryException = e => e is HttpRequestException, OnRetry = events.Add }));
        Assert.False(events[0].Outcome.HasResult);
        Assert.IsType<HttpRequestException>(events[0].Outcome.Exception);
        Assert.True(events[1].Outcome.HasResult);
    }

    [Fact]
    public async Task UnselectedExceptionAndCancellationDoNotRetry()
    {
        var error = new InvalidOperationException();
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Retry.ExecuteAsync<int>(_ => throw error, Options())));
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Retry.ExecuteAsync<int>(_ =>
        { attempts++; throw new OperationCanceledException(); }, Options() with { ShouldRetryException = _ => true }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RejectedResourcesAreDisposedAndFinalResourceBelongsToCaller()
    {
        var first = new Resource(); var final = new Resource(); var attempts = 0;
        Assert.Same(final, await Retry.ExecuteAsync(_ => Task.FromResult(++attempts == 1 ? first : final),
            new RetryOptions<Resource> { BaseDelay = TimeSpan.Zero, ShouldRetryResult = value => ReferenceEquals(value, first) }));
        Assert.Equal(1, first.Disposals);
        Assert.Equal(0, final.Disposals);
        final.Dispose();
    }

    [Fact]
    public async Task CallbackFailureDisposesRejectedResultAndStopsRetry()
    {
        var resource = new Resource(); var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Retry.ExecuteAsync(_ =>
        { attempts++; return Task.FromResult(resource); }, new RetryOptions<Resource>
        { ShouldRetryResult = _ => true, OnRetry = _ => throw new InvalidOperationException() }));
        Assert.Equal(1, attempts); Assert.Equal(1, resource.Disposals);
    }

    [Fact]
    public async Task PredicateFailureAndPredicateCancellationReleaseRejectedResult()
    {
        var resource = new Resource();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Retry.ExecuteAsync(_ => Task.FromResult(resource),
            new RetryOptions<Resource> { ShouldRetryResult = _ => throw new InvalidOperationException() }));
        Assert.Equal(1, resource.Disposals);
        using var source = new CancellationTokenSource(); var second = new Resource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Retry.ExecuteAsync(_ => Task.FromResult(second),
            new RetryOptions<Resource> { ShouldRetryResult = _ => { source.Cancel(); return true; } }, source.Token));
        Assert.Equal(1, second.Disposals);
    }

    [Fact]
    public async Task CustomCleanupReplacesAutomaticDisposal()
    {
        var resource = new Resource(); var cleanup = 0;
        await Retry.ExecuteAsync(_ => Task.FromResult(resource), new RetryOptions<Resource>
        {
            MaxRetries = 1, BaseDelay = TimeSpan.Zero, ShouldRetryResult = _ => true,
            OnDiscardResult = _ => { cleanup++; return ValueTask.CompletedTask; }
        });
        Assert.Equal(1, cleanup); Assert.Equal(0, resource.Disposals);
    }

    [Fact]
    public async Task FallbackReceivesSelectedExceptionAndProducesAlternative()
    {
        var error = new HttpRequestException();
        Assert.Equal("cached", await Fallback.ExecuteAsync<string>(_ => throw error, (outcome, _) =>
        { Assert.Same(error, outcome.Exception); Assert.False(outcome.HasResult); return Task.FromResult("cached"); },
            new FallbackOptions<string> { ShouldHandleException = ex => ex is HttpRequestException }));
    }

    [Fact]
    public async Task FallbackOnlyHandlesSelectedResultsAndErrors()
    {
        var calls = 0;
        var options = new FallbackOptions<int> { ShouldHandleResult = value => value < 0 };
        Task<int> Alternative(ResilienceOutcome<int> _, CancellationToken __) { calls++; return Task.FromResult(42); }
        Assert.Equal(42, await Fallback.ExecuteAsync(_ => Task.FromResult(-1), Alternative, options));
        Assert.Equal(7, await Fallback.ExecuteAsync(_ => Task.FromResult(7), Alternative, options));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Fallback.ExecuteAsync<int>(
            _ => throw new InvalidOperationException(), Alternative, options));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FallbackCancellationAndAlternativeErrorsArePropagated()
    {
        var calls = 0;
        var options = new FallbackOptions<int> { ShouldHandleException = _ => true };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fallback.ExecuteAsync<int>(
            _ => throw new OperationCanceledException(), (_, _) => { calls++; return Task.FromResult(1); }, options));
        Assert.Equal(0, calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Fallback.ExecuteAsync<int>(
            _ => throw new HttpRequestException(), (_, _) => { calls++; throw new InvalidOperationException(); }, options));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FallbackCanInspectRejectedResourceBeforeItIsDisposed()
    {
        var rejected = new Resource(); var replacement = new Resource();
        Assert.Same(replacement, await Fallback.ExecuteAsync(_ => Task.FromResult(rejected), (outcome, _) =>
        { Assert.Same(rejected, outcome.Result); Assert.Equal(0, rejected.Disposals); return Task.FromResult(replacement); },
            new FallbackOptions<Resource> { ShouldHandleResult = _ => true }));
        Assert.Equal(1, rejected.Disposals); Assert.Equal(0, replacement.Disposals);
    }

    [Fact]
    public async Task InvalidConfigurationsFailBeforeCallingOperation()
    {
        var attempts = 0;
        Task<int> Operation(CancellationToken _) { attempts++; return Task.FromResult(1); }
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Retry.ExecuteAsync(Operation, Options(-1)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Retry.ExecuteAsync(Operation, Options() with { ShouldRetryResult = null! }));
        await Assert.ThrowsAsync<ArgumentException>(() => Fallback.ExecuteAsync(Operation,
            (_, _) => Task.FromResult(1), new FallbackOptions<int>()));
        Assert.Equal(0, attempts);
    }

    internal sealed class Resource : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    [Fact]
    public async Task FallbackCleanupFailureAlsoReleasesAlternative()
    {
        var rejected = new Resource(); var alternative = new Resource();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Fallback.ExecuteAsync(_ => Task.FromResult(rejected),
            (_, _) => Task.FromResult(alternative), new FallbackOptions<Resource>
            {
                ShouldHandleResult = _ => true,
                OnDiscardResult = result =>
                {
                    result.Dispose();
                    if (ReferenceEquals(result, rejected)) throw new InvalidOperationException();
                    return ValueTask.CompletedTask;
                }
            }));
        Assert.Equal(1, rejected.Disposals); Assert.Equal(1, alternative.Disposals);
    }

    [Fact]
    public async Task AsyncDisposalIsPreferredOverSyncDisposal()
    {
        var rejected = new AsyncResource(); var final = new AsyncResource(); var attempts = 0;
        Assert.Same(final, await Retry.ExecuteAsync(_ => Task.FromResult(++attempts == 1 ? rejected : final),
            new RetryOptions<AsyncResource> { BaseDelay = TimeSpan.Zero, ShouldRetryResult = result => ReferenceEquals(result, rejected) }));
        Assert.Equal(1, rejected.AsyncDisposals); Assert.Equal(0, rejected.SyncDisposals);
    }

    private sealed class AsyncResource : IDisposable, IAsyncDisposable
    {
        public int AsyncDisposals { get; private set; }
        public int SyncDisposals { get; private set; }
        public void Dispose() => SyncDisposals++;
        public ValueTask DisposeAsync() { AsyncDisposals++; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task FallbackCanReturnTheSameResourceWithoutDisposingIt()
    {
        var resource = new Resource();
        Assert.Same(resource, await Fallback.ExecuteAsync(_ => Task.FromResult(resource),
            (outcome, _) => Task.FromResult(outcome.Result!), new FallbackOptions<Resource> { ShouldHandleResult = _ => true }));
        Assert.Equal(0, resource.Disposals);
    }
}
