using Microsoft.Extensions.Time.Testing;

namespace ResilienceLab.Tests;

public sealed class CircuitBreakerTests
{
    private static CircuitBreakerOptions Options(int threshold = 2) => new()
    {
        FailureThreshold = threshold,
        BreakDuration = TimeSpan.FromSeconds(30),
        ShouldHandle = error => error is HttpRequestException
    };

    private static TaskCompletionSource<int> Pending() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task<int> Failure(CancellationToken _) =>
        Task.FromException<int>(new HttpRequestException("Dependency unavailable."));

    [Fact]
    public void DefaultsAndValidation()
    {
        var defaults = new CircuitBreakerOptions { ShouldHandle = _ => true };
        Assert.Equal(5, defaults.FailureThreshold);
        Assert.Equal(TimeSpan.FromSeconds(30), defaults.BreakDuration);
        Assert.Throws<ArgumentNullException>(() => new CircuitBreaker(null!));
        Assert.Throws<ArgumentNullException>(() => new CircuitBreaker(Options() with { ShouldHandle = null! }));
        Assert.Throws<ArgumentNullException>(() => new CircuitBreaker(Options(), null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreaker(Options(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreaker(Options(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreaker(Options() with { BreakDuration = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreaker(Options() with { BreakDuration = TimeSpan.FromTicks(-1) }));
    }

    [Fact]
    public async Task OpensAtThresholdAndRejectsWithoutCallingOperationOrFilter()
    {
        var filterCalls = 0;
        var circuit = new CircuitBreaker(Options() with { ShouldHandle = _ => { filterCalls++; return true; } });
        var error = new HttpRequestException();

        Assert.Equal(CircuitState.Closed, circuit.State);
        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync<int>(_ => throw error));
        Assert.Same(error, thrown);
        Assert.Equal(CircuitState.Closed, circuit.State);
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        Assert.Equal(CircuitState.Open, circuit.State);

        var operationCalled = false;
        await Assert.ThrowsAsync<BrokenCircuitException>(() => circuit.ExecuteAsync(_ =>
        {
            operationCalled = true;
            return Task.FromResult(1);
        }));
        Assert.False(operationCalled);
        Assert.Equal(2, filterCalls);
    }

    [Fact]
    public async Task SuccessfulCallResetsConsecutiveFailures()
    {
        var circuit = new CircuitBreaker(Options());
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        Assert.Equal(42, await circuit.ExecuteAsync(_ => Task.FromResult(42)));
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        Assert.Equal(CircuitState.Closed, circuit.State);
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        Assert.Equal(CircuitState.Open, circuit.State);
    }

    [Fact]
    public async Task UnselectedErrorsNeitherCountNorResetSelectedFailures()
    {
        var circuit = new CircuitBreaker(Options());
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        await Assert.ThrowsAsync<InvalidOperationException>(() => circuit.ExecuteAsync<int>(_ => throw new InvalidOperationException()));
        Assert.Equal(CircuitState.Closed, circuit.State);
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        Assert.Equal(CircuitState.Open, circuit.State);
    }

    [Fact]
    public async Task CancellationNeverCountsOrInvokesFilter()
    {
        var filterCalls = 0;
        var circuit = new CircuitBreaker(Options(1) with { ShouldHandle = _ => { filterCalls++; return true; } });
        var error = new OperationCanceledException();
        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => circuit.ExecuteAsync<int>(_ => throw error));
        Assert.Same(error, thrown);
        Assert.Equal(CircuitState.Closed, circuit.State);
        Assert.Equal(0, filterCalls);
    }

    [Fact]
    public async Task FaultAfterConsumerCancellationDoesNotCount()
    {
        using var source = new CancellationTokenSource();
        var circuit = new CircuitBreaker(Options(1));
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync<int>(_ =>
        {
            source.Cancel();
            throw new HttpRequestException();
        }, source.Token));
        Assert.Equal(CircuitState.Closed, circuit.State);
    }

    [Fact]
    public async Task CancellationDuringPredicateDoesNotCountFailure()
    {
        using var source = new CancellationTokenSource();
        var circuit = new CircuitBreaker(Options(1) with
        {
            ShouldHandle = _ => { source.Cancel(); return true; }
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure, source.Token));
        Assert.Equal(CircuitState.Closed, circuit.State);
    }

    [Fact]
    public async Task PreCanceledCallDoesNotInvokeOperationOrConsumeRecoveryProbe()
    {
        var time = new FakeTimeProvider();
        var circuit = new CircuitBreaker(Options(1), time);
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        time.Advance(TimeSpan.FromSeconds(30));
        using var source = new CancellationTokenSource();
        source.Cancel();
        var called = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => circuit.ExecuteAsync(_ =>
        {
            called = true;
            return Task.FromResult(0);
        }, source.Token));
        Assert.False(called);
        Assert.Equal(42, await circuit.ExecuteAsync(_ => Task.FromResult(42)));
        Assert.Equal(CircuitState.Closed, circuit.State);
    }

    [Fact]
    public async Task ExpiryAllowsProbeWhichClosesAndResetsCounter()
    {
        var time = new FakeTimeProvider();
        var circuit = new CircuitBreaker(Options(), time);
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        time.Advance(TimeSpan.FromSeconds(29));
        await Assert.ThrowsAsync<BrokenCircuitException>(() => circuit.ExecuteAsync(_ => Task.FromResult(1)));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(CircuitState.HalfOpen, circuit.State);
        Assert.Equal(42, await circuit.ExecuteAsync(_ => Task.FromResult(42)));
        Assert.Equal(CircuitState.Closed, circuit.State);
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        Assert.Equal(CircuitState.Closed, circuit.State);
    }

    [Fact]
    public async Task FailedProbeStartsNewBreakDuration()
    {
        var time = new FakeTimeProvider();
        var circuit = new CircuitBreaker(Options(1), time);
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        time.Advance(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        Assert.Equal(CircuitState.Open, circuit.State);
        time.Advance(TimeSpan.FromSeconds(29));
        await Assert.ThrowsAsync<BrokenCircuitException>(() => circuit.ExecuteAsync(Failure));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await circuit.ExecuteAsync(_ => Task.FromResult(1)));
    }

    [Fact]
    public async Task OnlyOneRecoveryProbeCanRunConcurrently()
    {
        var time = new FakeTimeProvider();
        var circuit = new CircuitBreaker(Options(1), time);
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        time.Advance(TimeSpan.FromSeconds(30));

        var release = Pending();
        var entered = Pending();
        var attempts = 0;
        var probe = circuit.ExecuteAsync(_ =>
        {
            Interlocked.Increment(ref attempts);
            entered.TrySetResult(1);
            return release.Task;
        });
        await entered.Task;

        var contenders = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            await Assert.ThrowsAsync<BrokenCircuitException>(() => circuit.ExecuteAsync(_ =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromResult(0);
            }));
        }));
        await Task.WhenAll(contenders);
        Assert.Equal(1, attempts);
        Assert.Equal(CircuitState.HalfOpen, circuit.State);
        release.SetResult(42);
        Assert.Equal(42, await probe);
        Assert.Equal(CircuitState.Closed, circuit.State);
    }

    [Fact]
    public async Task ConcurrentAdmissionsAtExpiryElectExactlyOneProbe()
    {
        var time = new FakeTimeProvider();
        var circuit = new CircuitBreaker(Options(1), time);
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        time.Advance(TimeSpan.FromSeconds(30));
        var start = Pending();
        var release = Pending();
        var rejectionsFinished = Pending();
        var attempts = 0;
        var rejections = 0;
        var competitors = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                return (int?)await circuit.ExecuteAsync(_ =>
                {
                    Interlocked.Increment(ref attempts);
                    return release.Task;
                });
            }
            catch (BrokenCircuitException)
            {
                if (Interlocked.Increment(ref rejections) == 19)
                    rejectionsFinished.TrySetResult(1);
                return null;
            }
        })).ToArray();
        start.SetResult(1);
        try
        {
            await rejectionsFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref attempts));
        }
        finally
        {
            release.TrySetResult(42);
        }

        var results = await Task.WhenAll(competitors);
        Assert.Equal(42, Assert.Single(results, result => result.HasValue));
        Assert.Equal(CircuitState.Closed, circuit.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnselectedOrCanceledProbeReleasesSlotWithoutClosing(bool canceled)
    {
        var time = new FakeTimeProvider();
        var circuit = new CircuitBreaker(Options(1), time);
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        time.Advance(TimeSpan.FromSeconds(30));
        Exception error = canceled ? new OperationCanceledException() : new InvalidOperationException();
        var thrown = await Record.ExceptionAsync(() => circuit.ExecuteAsync<int>(_ => throw error));
        Assert.Same(error, thrown);
        Assert.Equal(CircuitState.HalfOpen, circuit.State);
        Assert.Equal(42, await circuit.ExecuteAsync(_ => Task.FromResult(42)));
        Assert.Equal(CircuitState.Closed, circuit.State);
    }

    [Fact]
    public async Task BrokenPredicatePreservesOriginalErrorAndReleasesProbe()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var circuit = new CircuitBreaker(Options(1) with
        {
            ShouldHandle = _ => ++calls == 1 ? true : throw new InvalidOperationException("Filter failed.")
        }, time);
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        time.Advance(TimeSpan.FromSeconds(30));
        var error = new HttpRequestException("Original.");
        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync<int>(_ => throw error));
        Assert.Same(error, thrown);
        Assert.Equal(CircuitState.HalfOpen, circuit.State);
        Assert.Equal(1, await circuit.ExecuteAsync(_ => Task.FromResult(1)));
    }

    [Fact]
    public async Task OldSuccessCannotCloseAnOpenCircuit()
    {
        var circuit = new CircuitBreaker(Options(1));
        var release = Pending();
        var oldCall = circuit.ExecuteAsync(_ => release.Task);
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        release.SetResult(42);
        Assert.Equal(42, await oldCall);
        Assert.Equal(CircuitState.Open, circuit.State);
    }

    [Fact]
    public async Task OldFailureCannotReopenCircuitAfterSuccessfulRecovery()
    {
        var time = new FakeTimeProvider();
        var circuit = new CircuitBreaker(Options(1), time);
        var release = Pending();
        var oldCall = circuit.ExecuteAsync(_ => release.Task);
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        time.Advance(TimeSpan.FromSeconds(30));
        await circuit.ExecuteAsync(_ => Task.FromResult(42));
        release.SetException(new HttpRequestException());
        await Assert.ThrowsAsync<HttpRequestException>(() => oldCall);
        Assert.Equal(CircuitState.Closed, circuit.State);
    }

    [Fact]
    public async Task OldSuccessCannotReleaseActiveRecoveryProbe()
    {
        var time = new FakeTimeProvider();
        var circuit = new CircuitBreaker(Options(1), time);
        var oldRelease = Pending();
        var oldCall = circuit.ExecuteAsync(_ => oldRelease.Task);
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        time.Advance(TimeSpan.FromSeconds(30));
        var probeRelease = Pending();
        var probe = circuit.ExecuteAsync(_ => probeRelease.Task);
        oldRelease.SetResult(1);
        await oldCall;
        await Assert.ThrowsAsync<BrokenCircuitException>(() => circuit.ExecuteAsync(_ => Task.FromResult(1)));
        probeRelease.SetResult(42);
        Assert.Equal(42, await probe);
    }

    [Fact]
    public async Task PredicateRunsOutsideStateLock()
    {
        using var release = new ManualResetEventSlim();
        var entered = Pending();
        var circuit = new CircuitBreaker(Options(1) with
        {
            ShouldHandle = _ =>
            {
                entered.SetResult(1);
                release.Wait();
                return true;
            }
        });
        var failingCall = Task.Run(() => circuit.ExecuteAsync(Failure));
        await entered.Task;
        try
        {
            // Timeout is a deadlock watchdog, not part of the behavior being measured.
            Assert.Equal(CircuitState.Closed,
                await Task.Run(() => circuit.State).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
        }
        await Assert.ThrowsAsync<HttpRequestException>(() => failingCall);
        Assert.Equal(CircuitState.Open, circuit.State);
    }

    [Fact]
    public async Task SynchronousOperationRunsOutsideStateLock()
    {
        using var release = new ManualResetEventSlim();
        var entered = Pending();
        var circuit = new CircuitBreaker(Options());
        var call = Task.Run(() => circuit.ExecuteAsync(_ =>
        {
            entered.SetResult(1);
            release.Wait();
            return Task.FromResult(42);
        }));
        await entered.Task;
        try
        {
            Assert.Equal(CircuitState.Closed,
                await Task.Run(() => circuit.State).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
        }
        Assert.Equal(42, await call);
    }

    [Fact]
    public async Task WallClockChangesDoNotEndBreakEarly()
    {
        var time = new SeparateClocks();
        var circuit = new CircuitBreaker(Options(1), time);
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failure));
        time.UtcNow += TimeSpan.FromDays(365);
        Assert.Equal(CircuitState.Open, circuit.State);
        time.Timestamp += TimeSpan.FromSeconds(30).Ticks;
        time.UtcNow -= TimeSpan.FromDays(730);
        Assert.Equal(CircuitState.HalfOpen, circuit.State);
        Assert.Equal(42, await circuit.ExecuteAsync(_ => Task.FromResult(42)));
    }

    [Fact]
    public async Task NullOperationIsRejected()
    {
        var circuit = new CircuitBreaker(Options());
        await Assert.ThrowsAsync<ArgumentNullException>(() => circuit.ExecuteAsync<int>(null!));
    }

    private sealed class SeparateClocks : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
        public long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
