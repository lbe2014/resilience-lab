using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ResilienceLab.Tests;

public class ObservabilityTests
{
    [Fact]
    public async Task RetryRecordsCountsDelaysAndStructuredLogsWithoutExceptionDetails()
    {
        const string name = "retry-observability";
        using var metrics = new Measurements(name);
        var logger = new RecordingLogger();
        var options = new RetryOptions
        {
            BaseDelay = TimeSpan.FromSeconds(2), MaxDelay = TimeSpan.FromSeconds(4),
            UseJitter = false, ShouldRetry = _ => true,
            Telemetry = new ResilienceTelemetry(name, logger)
        };
        var clock = new FakeTimeProvider();
        var signaledClock = new SignaledTimeProvider(clock);
        var attempts = 0;
        var task = Retry.ExecuteAsync(_ => ++attempts < 3
            ? Task.FromException<int>(new HttpRequestException("SENSITIVE secret URL"))
            : Task.FromResult(42), options, new ResilienceRuntime(signaledClock, () => 1));
        await signaledClock.TimerRegistered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(2));
        await signaledClock.TimerRegistered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(42, await task.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(2, metrics.Total("resilience.retry.count"));
        Assert.Equal(new[] { 2d, 4d }, metrics.All.Where(m => m.Name == "resilience.retry.delay").Select(m => m.Value));
        Assert.All(metrics.All, m => Assert.Equal("retry", m.Tags["resilience.strategy"]));
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal(1001, entry.EventId.Id);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Null(entry.Exception);
            Assert.DoesNotContain("SENSITIVE", entry.Message);
            Assert.Equal(name, entry.Fields["DependencyName"]);
        });
    }

    [Fact]
    public async Task SuccessfulAndCanceledRetriesEmitNoRetryMeasurements()
    {
        const string name = "retry-no-events";
        using var metrics = new Measurements(name);
        var options = new RetryOptions { ShouldRetry = _ => true, Telemetry = new(name) };
        Assert.Equal(1, await Retry.ExecuteAsync(_ => Task.FromResult(1), options));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Retry.ExecuteAsync<int>(
            _ => throw new OperationCanceledException(), options));
        Assert.Empty(metrics.All);
    }

    [Fact]
    public async Task LoggerFailureCannotBreakRetry()
    {
        var attempts = 0;
        var result = await Retry.ExecuteAsync(_ => ++attempts == 1
            ? Task.FromException<int>(new HttpRequestException()) : Task.FromResult(42),
            new RetryOptions { BaseDelay = TimeSpan.Zero, ShouldRetry = _ => true,
                Telemetry = new("broken-logger", new RecordingLogger { Throw = true }) });
        Assert.Equal(42, result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task FailingMetricListenerCannotBreakRetry()
    {
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == ResilienceTelemetry.MeterName)
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "dependency.name" && Equals(tag.Value, "broken-listener"))
                    throw new InvalidOperationException("Collector failed");
        });
        listener.Start();
        var attempts = 0;
        Assert.Equal(42, await Retry.ExecuteAsync(_ => ++attempts == 1
            ? Task.FromException<int>(new HttpRequestException()) : Task.FromResult(42),
            new RetryOptions { BaseDelay = TimeSpan.Zero, ShouldRetry = _ => true,
                Telemetry = new("broken-listener") }));
    }

    [Fact]
    public async Task TimeoutRecordsOneRejectionAfterOperationSettles()
    {
        const string name = "timeout-observability";
        using var metrics = new Measurements(name);
        var logger = new RecordingLogger();
        var clock = new FakeTimeProvider();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = ResilienceLab.Timeout.ExecuteAsync(_ => completion.Task,
            new TimeoutOptions { TimeoutDuration = TimeSpan.FromSeconds(1), Telemetry = new(name, logger) }, clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, metrics.Total("resilience.timeout.count"));
        completion.SetResult(42);
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, metrics.Total("resilience.timeout.count"));
        Assert.Equal(1002, Assert.Single(logger.Entries).EventId.Id);
    }

    [Fact]
    public async Task ConsumerCancellationDoesNotRecordTimeout()
    {
        const string name = "timeout-cancellation";
        using var metrics = new Measurements(name);
        var clock = new FakeTimeProvider();
        using var source = new CancellationTokenSource();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = ResilienceLab.Timeout.ExecuteAsync(_ => completion.Task,
            new TimeoutOptions { TimeoutDuration = TimeSpan.FromSeconds(1), Telemetry = new(name) }, clock, source.Token);
        clock.Advance(TimeSpan.FromSeconds(1));
        source.Cancel();
        completion.SetResult(1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(metrics.All);
    }

    [Fact]
    public async Task CircuitReportsCommittedTransitionsOnceAndCountsRejections()
    {
        const string name = "circuit-observability";
        using var metrics = new Measurements(name);
        var logger = new RecordingLogger();
        var events = new List<CircuitStateChangedEvent>();
        var clock = new FakeTimeProvider();
        var circuit = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 1, BreakDuration = TimeSpan.FromSeconds(1),
            ShouldHandle = _ => true, Telemetry = new(name, logger), OnStateChanged = events.Add
        }, clock);
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync<int>(_ => throw new HttpRequestException()));
        Assert.Equal(CircuitState.Open, circuit.State);
        await Assert.ThrowsAsync<BrokenCircuitException>(() => circuit.ExecuteAsync(_ => Task.FromResult(1)));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(CircuitState.HalfOpen, circuit.State);
        Assert.Equal(CircuitState.HalfOpen, circuit.State);
        await circuit.ExecuteAsync(_ => Task.FromResult(1));
        Assert.Equal(new[] { CircuitState.Open, CircuitState.HalfOpen, CircuitState.Closed }, events.Select(e => e.CurrentState));
        Assert.Equal(new long[] { 1, 2, 3 }, events.Select(e => e.Sequence));
        Assert.Equal(CircuitState.Closed, events[0].PreviousState);
        Assert.Equal(clock.GetUtcNow(), events[1].Timestamp);
        Assert.Equal(3, metrics.Total("resilience.circuit.transition.count"));
        Assert.Equal(1, metrics.Total("resilience.circuit.rejection.count"));
        Assert.Equal(4, logger.Entries.Count);
    }

    [Fact]
    public async Task ThrowingCircuitObserversDoNotReplaceErrorsOrStrandProbe()
    {
        var clock = new FakeTimeProvider();
        var circuit = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 1, BreakDuration = TimeSpan.FromSeconds(1), ShouldHandle = _ => true,
            Telemetry = new("circuit-broken-observers", new RecordingLogger { Throw = true }),
            OnStateChanged = _ => throw new InvalidOperationException("Observer failure")
        }, clock);
        var error = new HttpRequestException();
        Assert.Same(error, await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync<int>(_ => throw error)));
        await Assert.ThrowsAsync<BrokenCircuitException>(() => circuit.ExecuteAsync(_ => Task.FromResult(1)));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(42, await circuit.ExecuteAsync(_ => Task.FromResult(42)));
        Assert.Equal(CircuitState.Closed, circuit.State);
    }

    [Fact]
    public async Task CircuitObserverRunsOutsideLockAndCanReadState()
    {
        CircuitBreaker? circuit = null;
        Exception? callbackFailure = null;
        var observed = false;
        circuit = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 1, ShouldHandle = _ => true,
            OnStateChanged = transition =>
            {
                // A second thread reading State would deadlock if the callback held the state lock.
                try
                {
                    var state = Task.Run(() => circuit!.State).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    Assert.Equal(transition.CurrentState, state);
                    observed = true;
                }
                catch (Exception error) { callbackFailure = error; }
            }
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync<int>(_ => throw new HttpRequestException()));
        Assert.Equal(CircuitState.Open, circuit.State);
        Assert.Null(callbackFailure);
        Assert.True(observed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void TelemetryRequiresStableName(string name) =>
        Assert.Throws<ArgumentException>(() => new ResilienceTelemetry(name));

    [Fact]
    public async Task ConcurrentRecoveryEmitsOnlyOneHalfOpenAndOneClosedTransition()
    {
        const string name = "circuit-concurrent-metrics";
        using var metrics = new Measurements(name);
        var events = new ConcurrentQueue<CircuitStateChangedEvent>();
        var clock = new FakeTimeProvider();
        var circuit = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 1, BreakDuration = TimeSpan.FromSeconds(1), ShouldHandle = _ => true,
            Telemetry = new(name), OnStateChanged = events.Enqueue
        }, clock);
        await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync<int>(_ => throw new HttpRequestException()));
        clock.Advance(TimeSpan.FromSeconds(1));
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejected = 0;
        var rejectedAll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var calls = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            try
            {
                await circuit.ExecuteAsync(_ => { Interlocked.Increment(ref attempts); return release.Task; });
            }
            catch (BrokenCircuitException)
            {
                if (Interlocked.Increment(ref rejected) == 9) rejectedAll.TrySetResult();
            }
        })).ToArray();
        try { await rejectedAll.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetResult(42); }
        await Task.WhenAll(calls);
        Assert.Equal(1, attempts);
        Assert.Equal(new long[] { 1, 2, 3 }, events.Select(e => e.Sequence).Order());
        Assert.Equal(3, metrics.Total("resilience.circuit.transition.count"));
        Assert.Equal(9, metrics.Total("resilience.circuit.rejection.count"));
    }

    private sealed class SignaledTimeProvider(FakeTimeProvider clock) : TimeProvider
    {
        public Channel<TimeSpan> TimerRegistered { get; } = Channel.CreateUnbounded<TimeSpan>();
        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();
        public override long GetTimestamp() => clock.GetTimestamp();
        public override long TimestampFrequency => clock.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = clock.CreateTimer(callback, state, dueTime, period);
            TimerRegistered.Writer.TryWrite(dueTime);
            return timer;
        }
    }

    private sealed record Measurement(string Name, double Value, Dictionary<string, object?> Tags);

    private sealed class Measurements : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<Measurement> _values = new();
        public Measurement[] All => _values.ToArray();
        public double Total(string instrument) => All.Where(m => m.Name == instrument).Sum(m => m.Value);

        public Measurements(string dependency)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ResilienceTelemetry.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags, dependency));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags, dependency));
            _listener.Start();
        }

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, string dependency)
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags) copy[tag.Key] = tag.Value;
            if (Equals(copy.GetValueOrDefault("dependency.name"), dependency))
                _values.Enqueue(new(instrument.Name, value, copy));
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed record Entry(LogLevel Level, EventId EventId, string Message, Exception? Exception,
        Dictionary<string, object?> Fields);

    private sealed class RecordingLogger : ILogger
    {
        public List<Entry> Entries { get; } = new();
        public bool Throw { get; init; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Throw) throw new InvalidOperationException("Logger unavailable");
            var fields = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(x => x.Key, x => x.Value) : new();
            Entries.Add(new(logLevel, eventId, formatter(state, exception), exception, fields));
        }
    }
}
