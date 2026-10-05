using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using System.Threading.RateLimiting;

namespace ResilienceLab.Tests;

public class PipelineTests
{
    private static RetryOptions RetryOptions => new()
    {
        MaxRetries = 2, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero,
        ShouldRetry = error => error is HttpRequestException or TimeoutRejectedException
    };
    private static CircuitBreakerOptions CircuitOptions => new()
    {
        FailureThreshold = 1, BreakDuration = TimeSpan.FromMinutes(1),
        ShouldHandle = error => error is HttpRequestException
    };

    [Fact]
    public async Task EmptyPipelineReturnsTheOperationResult()
    {
        var result = new object();
        Assert.Same(result, await new ResiliencePipelineBuilder<object>().Build().ExecuteAsync(_ => Task.FromResult(result)));
    }

    [Fact]
    public async Task CircuitOutsideRetryCountsFinalFailureAndPersistsAcrossCalls()
    {
        var pipeline = new ResiliencePipelineBuilder<int>().AddCircuitBreaker(CircuitOptions).AddRetry(RetryOptions).Build();
        var calls = 0;
        Task<int> Fail(CancellationToken _) { calls++; return Task.FromException<int>(new HttpRequestException()); }
        await Assert.ThrowsAsync<HttpRequestException>(() => pipeline.ExecuteAsync(Fail));
        Assert.Equal(3, calls);
        await Assert.ThrowsAsync<BrokenCircuitException>(() => pipeline.ExecuteAsync(Fail));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task CircuitInsideRetryOpensAfterFirstAttempt()
    {
        var calls = 0;
        var pipeline = new ResiliencePipelineBuilder<int>().AddRetry(RetryOptions).AddCircuitBreaker(CircuitOptions).Build();
        await Assert.ThrowsAsync<BrokenCircuitException>(() => pipeline.ExecuteAsync(_ =>
        {
            calls++;
            return Task.FromException<int>(new HttpRequestException());
        }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FallbackObservesExhaustionAndOpenCircuit()
    {
        var pipeline = new ResiliencePipelineBuilder<int>()
            .AddFallback((_, _) => Task.FromResult(42), new FallbackOptions<int>
            { ShouldHandleException = error => error is HttpRequestException or BrokenCircuitException })
            .AddCircuitBreaker(CircuitOptions).AddRetry(RetryOptions).Build();
        var calls = 0;
        Task<int> Fail(CancellationToken _) { calls++; return Task.FromException<int>(new HttpRequestException()); }
        Assert.Equal(42, await pipeline.ExecuteAsync(Fail));
        Assert.Equal(42, await pipeline.ExecuteAsync(Fail));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task BuildsHaveIndependentCircuitStateAndSnapshotRegistrations()
    {
        var builder = new ResiliencePipelineBuilder<int>().AddCircuitBreaker(CircuitOptions);
        var first = builder.Build();
        var second = builder.Build();
        builder.AddFallback((_, _) => Task.FromResult(99), new FallbackOptions<int> { ShouldHandleException = _ => true });
        await Assert.ThrowsAsync<HttpRequestException>(() => first.ExecuteAsync(_ => Task.FromException<int>(new HttpRequestException())));
        Assert.Equal(7, await second.ExecuteAsync(_ => Task.FromResult(7)));
        await Assert.ThrowsAsync<HttpRequestException>(() => second.ExecuteAsync(_ => Task.FromException<int>(new HttpRequestException())));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TimeoutOrderDistinguishesPerAttemptFromTotal(bool perAttempt)
    {
        var clock = new FakeTimeProvider();
        var builder = new ResiliencePipelineBuilder<int>(new ResilienceRuntime(clock, () => 0));
        if (perAttempt) builder.AddRetry(RetryOptions).AddTimeout(TimeSpan.FromSeconds(1));
        else builder.AddTimeout(TimeSpan.FromSeconds(1)).AddRetry(RetryOptions);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var running = builder.Build().ExecuteAsync(async token =>
        {
            if (++calls > 1) return 42;
            var waiting = Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            started.SetResult();
            await waiting;
            return 0;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        if (perAttempt) Assert.Equal(42, await running.WaitAsync(TimeSpan.FromSeconds(5)));
        else await Assert.ThrowsAsync<TimeoutRejectedException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(perAttempt ? 2 : 1, calls);
    }

    [Fact]
    public async Task ResultRetryAndHedgingCanBeComposed()
    {
        var calls = 0;
        var pipeline = new ResiliencePipelineBuilder<int>()
            .AddRetry(new RetryOptions<int> { BaseDelay = TimeSpan.Zero, ShouldRetryResult = value => value < 0 })
            .AddHedging(new HedgingOptions<int> { MaxHedgedAttempts = 0 }).Build();
        Assert.Equal(42, await pipeline.ExecuteAsync(_ => Task.FromResult(++calls == 1 ? -1 : 42)));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RateLimiterBudgetIsSharedAndRemainsConsumerOwned()
    {
        using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1, TokensPerPeriod = 1, AutoReplenishment = false,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1), QueueLimit = 0
        });
        var builder = new ResiliencePipelineBuilder<int>().AddRateLimit(limiter);
        Assert.Equal(1, await builder.Build().ExecuteAsync(_ => Task.FromResult(1)));
        await Assert.ThrowsAsync<RateLimitRejectedException>(() => builder.Build().ExecuteAsync(_ => Task.FromResult(2)));
        using var lease = await limiter.AcquireAsync();
        Assert.False(lease.IsAcquired); // Still usable, not disposed by the pipeline.
    }

    [Fact]
    public async Task CancellationIsNotConvertedToFallbackOrRetried()
    {
        var calls = 0;
        var pipeline = new ResiliencePipelineBuilder<int>()
            .AddFallback((_, _) => Task.FromResult(99), new FallbackOptions<int> { ShouldHandleException = _ => true })
            .AddRetry(RetryOptions).Build();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ExecuteAsync(_ =>
        { calls++; return Task.FromResult(1); }, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ExecuteAsync(_ =>
        { calls++; return Task.FromException<int>(new OperationCanceledException()); }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NamedRegistrationSharesSingletonAndSeparatesNamesAndTypes()
    {
        var services = new ServiceCollection();
        var configurations = 0;
        services.AddResiliencePipeline<int>("api", (_, builder) => { configurations++; builder.AddCircuitBreaker(CircuitOptions); });
        services.AddResiliencePipeline<int>("other", builder => builder.AddCircuitBreaker(CircuitOptions));
        services.AddResiliencePipeline<string>("api", _ => { });
        using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredKeyedService<ResiliencePipeline<int>>("api");
        Assert.Same(pipeline, provider.GetRequiredKeyedService<ResiliencePipeline<int>>("api"));
        Assert.Equal(1, configurations);
        await Assert.ThrowsAsync<HttpRequestException>(() => pipeline.ExecuteAsync(_ => Task.FromException<int>(new HttpRequestException())));
        await Assert.ThrowsAsync<BrokenCircuitException>(() => provider.GetRequiredKeyedService<ResiliencePipeline<int>>("api").ExecuteAsync(_ => Task.FromResult(1)));
        Assert.Equal(2, await provider.GetRequiredKeyedService<ResiliencePipeline<int>>("other").ExecuteAsync(_ => Task.FromResult(2)));
        Assert.Equal("ok", await provider.GetRequiredKeyedService<ResiliencePipeline<string>>("api").ExecuteAsync(_ => Task.FromResult("ok")));
    }

    [Fact]
    public async Task ConcurrentCallsHaveIndependentRetryBudgets()
    {
        var pipeline = new ResiliencePipelineBuilder<int>().AddRetry(RetryOptions).Build();
        async Task<int> Run()
        {
            var calls = 0;
            return await pipeline.ExecuteAsync(async _ =>
            {
                await Task.Yield();
                if (++calls < 3) throw new HttpRequestException();
                return calls;
            });
        }
        Assert.Equal(new[] { 3, 3, 3 }, await Task.WhenAll(Run(), Run(), Run()));
    }

    [Fact]
    public async Task HedgingCancelsAndDrainsTheLosingOperation()
    {
        var calls = 0;
        var loserFinished = false;
        var pipeline = new ResiliencePipelineBuilder<int>()
            .AddHedging(new HedgingOptions<int> { Delay = TimeSpan.Zero }).Build();
        var result = await pipeline.ExecuteAsync(async token =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token); }
                finally { loserFinished = true; }
            }
            return 42;
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(42, result);
        Assert.Equal(2, calls);
        Assert.True(loserFinished);
    }

    [Fact]
    public void InvalidRegistrationsAndOperationsAreRejected()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddResiliencePipeline<int>(" ", _ => { }));
        services.AddResiliencePipeline<int>("api", _ => { });
        Assert.Throws<ArgumentException>(() => services.AddResiliencePipeline<int>("api", _ => { }));
        Assert.Throws<ArgumentNullException>(() => new ResiliencePipelineBuilder<int>().AddRetry((RetryOptions)null!));
        Assert.Throws<ArgumentNullException>(() => { _ = new ResiliencePipelineBuilder<int>().Build().ExecuteAsync(null!); });
    }
}
