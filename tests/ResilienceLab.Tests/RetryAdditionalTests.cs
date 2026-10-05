using Microsoft.Extensions.Time.Testing;

namespace ResilienceLab.Tests;

public class RetryAdditionalTests
{
    private static RetryOptions Options() => new()
    {
        MaxRetries = 3,
        BaseDelay = TimeSpan.Zero,
        MaxDelay = TimeSpan.Zero,
        ShouldRetry = error => error is HttpRequestException
    };

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 200)]
    [InlineData(2, 400)]
    [InlineData(3, 750)]
    [InlineData(20, 750)]
    [InlineData(int.MaxValue, 750)]
    public void BackoffDoublesUntilTheConfiguredCap(int retryIndex, double expectedMilliseconds)
    {
        var delay = RetryDelay.Calculate(TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(750), retryIndex, 1);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), delay);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.25, 187.5)]
    [InlineData(0.5, 375)]
    [InlineData(1, 750)]
    public void FullJitterScalesTheCappedDelay(double sample, double expectedMilliseconds)
    {
        var delay = RetryDelay.Calculate(TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(750), 20, sample);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), delay);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void ZeroBaseDelayStaysZeroEvenForVeryLargeRetryIndexes(int retryIndex)
    {
        Assert.Equal(TimeSpan.Zero, RetryDelay.Calculate(TimeSpan.Zero,
            TimeSpan.FromSeconds(10), retryIndex, 1));
    }

    [Fact]
    public async Task JitterControlsTheActualWaitWithSimulatedTime()
    {
        var clock = new FakeTimeProvider();
        var samples = 0;
        var runtime = new ResilienceRuntime(clock, () => { samples++; return 0.25; });
        var attempts = 0;
        RetryEvent? notification = null;
        var task = Retry.ExecuteAsync(_ => ++attempts == 1
                ? Task.FromException<int>(new HttpRequestException()) : Task.FromResult(42),
            Options() with
            {
                BaseDelay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromSeconds(1),
                OnRetry = retry => notification = retry
            }, runtime);

        Assert.Equal(1, attempts);
        Assert.NotNull(notification);
        Assert.Equal(TimeSpan.FromMilliseconds(25), notification.Delay);
        clock.Advance(TimeSpan.FromMilliseconds(24));
        Assert.False(task.IsCompleted);
        Assert.Equal(1, attempts);
        clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(42, await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, attempts);
        Assert.Equal(1, samples);
    }

    [Fact]
    public async Task DisablingJitterUsesTheEntireDelayWithoutSampling()
    {
        var clock = new FakeTimeProvider();
        var runtime = new ResilienceRuntime(clock, () => throw new InvalidOperationException("Unexpected sample."));
        var attempts = 0;
        var task = Retry.ExecuteAsync(_ => ++attempts == 1
                ? Task.FromException<int>(new HttpRequestException()) : Task.FromResult(42),
            Options() with
            {
                BaseDelay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromMilliseconds(100),
                UseJitter = false
            }, runtime);

        clock.Advance(TimeSpan.FromMilliseconds(99));
        Assert.False(task.IsCompleted);
        Assert.Equal(1, attempts);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(42, await task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task EventsReportTheErrorAndUpcomingRetryBeforeTheNextAttempt()
    {
        var attempts = 0;
        var firstError = new HttpRequestException("First failure.");
        var secondError = new HttpRequestException("Second failure.");
        var events = new List<RetryEvent>();
        var result = await Retry.ExecuteAsync(_ => ++attempts switch
        {
            1 => Task.FromException<int>(firstError),
            2 => Task.FromException<int>(secondError),
            _ => Task.FromResult(42)
        }, Options() with
        {
            OnRetry = retry =>
            {
                Assert.Equal(attempts, retry.RetryNumber);
                events.Add(retry);
            }
        });

        Assert.Equal(42, result);
        Assert.Collection(events,
            retry =>
            {
                Assert.Equal(1, retry.RetryNumber);
                Assert.Same(firstError, retry.Exception);
                Assert.Equal(TimeSpan.Zero, retry.Delay);
            },
            retry =>
            {
                Assert.Equal(2, retry.RetryNumber);
                Assert.Same(secondError, retry.Exception);
            });
    }

    [Fact]
    public async Task ExhaustionDoesNotReportAnAdditionalRetry()
    {
        var events = new List<RetryEvent>();
        var error = new HttpRequestException();
        await Assert.ThrowsAsync<HttpRequestException>(() => Retry.ExecuteAsync<int>(
            _ => Task.FromException<int>(error), Options() with { MaxRetries = 2, OnRetry = events.Add }));

        Assert.Equal(new[] { 1, 2 }, events.Select(retry => retry.RetryNumber));
        Assert.All(events, retry => Assert.Same(error, retry.Exception));
    }

    [Fact]
    public async Task SuccessUnselectedErrorAndCancellationDoNotEmitEvents()
    {
        var events = new List<RetryEvent>();
        var options = Options() with { OnRetry = events.Add };
        await Retry.ExecuteAsync(_ => Task.FromResult(42), options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Retry.ExecuteAsync<int>(
            _ => Task.FromException<int>(new InvalidOperationException()), options));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Retry.ExecuteAsync<int>(
            _ => Task.FromException<int>(new OperationCanceledException()),
            options with { ShouldRetry = _ => true }));

        Assert.Empty(events);
    }

    [Fact]
    public async Task CallbackFailureStopsRetriesAndPropagatesTheCallbackException()
    {
        var attempts = 0;
        var callbackError = new InvalidOperationException("Cannot record retry.");
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => Retry.ExecuteAsync<int>(
            _ => { attempts++; throw new HttpRequestException(); },
            Options() with { OnRetry = _ => throw callbackError }));

        Assert.Same(callbackError, thrown);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CancellationFromPredicatePreventsNotificationAndAnotherAttempt()
    {
        using var source = new CancellationTokenSource();
        var attempts = 0;
        var events = new List<RetryEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Retry.ExecuteAsync<int>(
            _ => { attempts++; throw new HttpRequestException(); },
            Options() with
            {
                ShouldRetry = _ => { source.Cancel(); return true; },
                OnRetry = events.Add
            }, source.Token));

        Assert.Equal(1, attempts);
        Assert.Empty(events);
    }

    [Fact]
    public async Task CancellationFromCallbackPreventsAnotherAttemptEvenWithZeroDelay()
    {
        using var source = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Retry.ExecuteAsync<int>(
            _ => { attempts++; throw new HttpRequestException(); },
            Options() with { OnRetry = _ => source.Cancel() }, source.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CancellationInterruptsTheSimulatedWaitWithoutAdvancingTime()
    {
        using var source = new CancellationTokenSource();
        var clock = new FakeTimeProvider();
        var runtime = new ResilienceRuntime(clock, () => 1);
        var attempts = 0;
        var task = Retry.ExecuteAsync<int>(_ => { attempts++; throw new HttpRequestException(); },
            Options() with { BaseDelay = TimeSpan.FromSeconds(10), MaxDelay = TimeSpan.FromSeconds(10) },
            runtime, source.Token);

        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, attempts);
    }
}
