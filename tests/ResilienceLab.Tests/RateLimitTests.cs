using System.Threading.RateLimiting;

namespace ResilienceLab.Tests;

public class RateLimitTests
{
    private static ConcurrencyLimiter Limiter(int queue = 0) => new(new ConcurrencyLimiterOptions
    { PermitLimit = 1, QueueLimit = queue, QueueProcessingOrder = QueueProcessingOrder.OldestFirst });

    [Fact]
    public async Task RejectsWithoutInvokingOperationWhenPermitIsOccupied()
    {
        using var limiter = Limiter(); var strategy = new RateLimit(limiter);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = strategy.ExecuteAsync(_ => release.Task);
        var calls = 0;
        await Assert.ThrowsAsync<RateLimitRejectedException>(() => strategy.ExecuteAsync(_ =>
        { calls++; return Task.FromResult(1); }));
        Assert.Equal(0, calls);
        release.SetResult(42); Assert.Equal(42, await first);
        Assert.Equal(7, await strategy.ExecuteAsync(_ => Task.FromResult(7)));
    }

    [Fact]
    public async Task QueueWaitsForPermitAndCancellationRemovesQueuedOperation()
    {
        using var limiter = Limiter(1); var strategy = new RateLimit(limiter);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = strategy.ExecuteAsync(_ => release.Task);
        using var source = new CancellationTokenSource();
        var calls = 0;
        var queued = strategy.ExecuteAsync(_ => { calls++; return Task.FromResult(1); }, source.Token);
        Assert.False(queued.IsCompleted);
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(0, calls);
        var next = strategy.ExecuteAsync(_ => Task.FromResult(7));
        Assert.False(next.IsCompleted);
        release.SetResult(42);
        await first; Assert.Equal(7, await next);
    }

    [Fact]
    public async Task OperationErrorReleasesPermit()
    {
        using var limiter = Limiter(); var strategy = new RateLimit(limiter);
        await Assert.ThrowsAsync<HttpRequestException>(() => strategy.ExecuteAsync<int>(_ => throw new HttpRequestException()));
        Assert.Equal(42, await strategy.ExecuteAsync(_ => Task.FromResult(42)));
    }

    [Fact]
    public async Task TokenBucketEnforcesRateBudgetAndReportsRetryAfter()
    {
        using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 2, TokensPerPeriod = 2, ReplenishmentPeriod = TimeSpan.FromHours(1),
            AutoReplenishment = false, QueueLimit = 0
        });
        var strategy = new RateLimit(limiter);
        await strategy.ExecuteAsync(_ => Task.FromResult(1));
        await strategy.ExecuteAsync(_ => Task.FromResult(2));
        var error = await Assert.ThrowsAsync<RateLimitRejectedException>(() => strategy.ExecuteAsync(_ => Task.FromResult(3)));
        Assert.NotNull(error.RetryAfter);
        Assert.True(error.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task PreCanceledTokenAndDisposedLimiterDoNotInvokeOperation()
    {
        using var limiter = Limiter(); var strategy = new RateLimit(limiter);
        var calls = 0;
        using var source = new CancellationTokenSource(); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => strategy.ExecuteAsync(_ =>
        { calls++; return Task.FromResult(1); }, source.Token));
        limiter.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => strategy.ExecuteAsync(_ =>
        { calls++; return Task.FromResult(1); }));
        Assert.Equal(0, calls);
    }
}
