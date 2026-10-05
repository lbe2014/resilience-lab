using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using System.Threading.RateLimiting;

namespace ResilienceLab.Tests;

[CollectionDefinition("Tracing", DisableParallelization = true)]
public class TracingCollection { }

[Collection("Tracing")]
public class TracingTests
{
    private sealed class Capture : IDisposable
    {
        private readonly ActivityListener _listener;
        public ConcurrentQueue<Activity> Spans { get; } = new();
        public Capture(ActivitySamplingResult sampling = ActivitySamplingResult.AllDataAndRecorded)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ResilienceTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampling,
                ActivityStopped = activity => Spans.Enqueue(activity)
            };
            ActivitySource.AddActivityListener(_listener);
        }
        public void Dispose() => _listener.Dispose();
    }
    private static RetryOptions RetryOptions => new()
    {
        MaxRetries = 1, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero,
        ShouldRetry = error => error is HttpRequestException
    };

    [Fact]
    public async Task PipelineAndAttemptsShareParentTraceAndRecoverWithoutPipelineError()
    {
        using var capture = new Capture();
        using var parent = new Activity("incoming").Start();
        var pipeline = new ResiliencePipelineBuilder<int>().WithTelemetry(new("trace-test"))
            .AddRetry(RetryOptions).Build();
        var calls = 0;
        Assert.Equal(42, await pipeline.ExecuteAsync(_ => ++calls == 1
            ? Task.FromException<int>(new HttpRequestException("secret-token")) : Task.FromResult(42)));
        Assert.Same(parent, Activity.Current);
        var root = Assert.Single(capture.Spans, span => span.OperationName == "resilience.pipeline");
        var retry = Assert.Single(capture.Spans, span => span.OperationName == "resilience.retry");
        var attempts = capture.Spans.Where(span => span.OperationName == "resilience.retry.attempt").ToArray();
        Assert.Equal(parent.SpanId, root.ParentSpanId);
        Assert.Equal(root.SpanId, retry.ParentSpanId);
        Assert.Equal(2, attempts.Length);
        Assert.All(attempts, span => { Assert.Equal(retry.SpanId, span.ParentSpanId); Assert.Equal(parent.TraceId, span.TraceId); });
        Assert.Equal(ActivityStatusCode.Error, attempts[0].Status);
        Assert.Equal(ActivityStatusCode.Unset, root.Status);
        Assert.Contains(retry.Events, item => item.Name == "retry.scheduled");
        Assert.Equal(new[] { 0, 1 }, attempts.Select(span => (int)span.GetTagItem("resilience.attempt")!).ToArray());
        Assert.All(capture.Spans, span =>
        {
            Assert.DoesNotContain("secret-token", string.Join(" ", span.TagObjects.Select(tag => tag.Value)));
            Assert.Null(span.StatusDescription);
        });
    }

    [Fact]
    public async Task FallbackAndCircuitEventsRemainInTheOwningSpan()
    {
        using var capture = new Capture();
        var pipeline = new ResiliencePipelineBuilder<int>().WithTelemetry(new("trace-test"))
            .AddFallback((_, _) => Task.FromResult(42), new FallbackOptions<int>
            { ShouldHandleException = error => error is HttpRequestException or BrokenCircuitException })
            .AddCircuitBreaker(new CircuitBreakerOptions
            { FailureThreshold = 1, BreakDuration = TimeSpan.FromMinutes(1), ShouldHandle = _ => true }).Build();
        Assert.Equal(42, await pipeline.ExecuteAsync(_ => Task.FromException<int>(new HttpRequestException())));
        Assert.Equal(42, await pipeline.ExecuteAsync(_ => Task.FromResult(0)));
        Assert.Contains(capture.Spans, span => span.Events.Any(item => item.Name == "circuit.changed"));
        Assert.Contains(capture.Spans, span => span.Events.Any(item => item.Name == "circuit.rejected"));
        Assert.Equal(2, capture.Spans.Count(span => span.OperationName == "resilience.fallback.execute"));
        Assert.All(capture.Spans.Where(span => span.OperationName == "resilience.pipeline"), span => Assert.Equal("success", span.GetTagItem("resilience.outcome")));
    }

    [Fact]
    public async Task TimeoutIsAnErrorButCallerCancellationIsNot()
    {
        using var capture = new Capture();
        var clock = new FakeTimeProvider();
        var pipeline = new ResiliencePipelineBuilder<int>(new ResilienceRuntime(clock, () => 0))
            .WithTelemetry(new("trace-test")).AddTimeout(TimeSpan.FromSeconds(1)).Build();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = pipeline.ExecuteAsync(async token =>
        {
            var waiting = Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            started.SetResult();
            await waiting;
            return 0;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => running);
        Assert.Contains(capture.Spans, span => span.Events.Any(item => item.Name == "timeout.rejected"));
        Assert.All(capture.Spans, span => Assert.Equal(ActivityStatusCode.Error, span.Status));
        capture.Spans.Clear();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ExecuteAsync(_ => Task.FromException<int>(new OperationCanceledException())));
        Assert.All(capture.Spans, span =>
        { Assert.Equal("canceled", span.GetTagItem("resilience.outcome")); Assert.Equal(ActivityStatusCode.Unset, span.Status); });
    }

    [Fact]
    public async Task HedgingAlternativesAreSiblingSpansAndLoserEndsBeforeRoot()
    {
        using var capture = new Capture();
        var calls = 0;
        var pipeline = new ResiliencePipelineBuilder<int>().WithTelemetry(new("trace-test"))
            .AddHedging(new HedgingOptions<int> { Delay = TimeSpan.Zero }).Build();
        Assert.Equal(42, await pipeline.ExecuteAsync(async token =>
        {
            if (Interlocked.Increment(ref calls) == 1) await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            return 42;
        }).WaitAsync(TimeSpan.FromSeconds(5)));
        var hedge = Assert.Single(capture.Spans, span => span.OperationName == "resilience.hedging");
        var attempts = capture.Spans.Where(span => span.OperationName == "resilience.hedging.attempt").ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.All(attempts, span => Assert.Equal(hedge.SpanId, span.ParentSpanId));
        Assert.Contains(attempts, span => Equals(span.GetTagItem("resilience.outcome"), "canceled"));
        Assert.Contains(hedge.Events, item => item.Name == "hedging.won");
        Assert.Equal("resilience.pipeline", capture.Spans.Last().OperationName);
    }

    [Fact]
    public async Task ResultRetryAndRateLimitProduceEventsWithoutSerializingResults()
    {
        using var capture = new Capture();
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var calls = 0;
        var pipeline = new ResiliencePipelineBuilder<string>().WithTelemetry(new("trace-test"))
            .AddRetry(new RetryOptions<string> { BaseDelay = TimeSpan.Zero, ShouldRetryResult = value => value == "secret-result" })
            .AddRateLimit(limiter).Build();
        Assert.Equal("ok", await pipeline.ExecuteAsync(_ => Task.FromResult(++calls == 1 ? "secret-result" : "ok")));
        Assert.Contains(capture.Spans, span => span.OperationName == "resilience.result_retry" && span.Events.Any(item => item.Name == "retry.scheduled"));
        Assert.Equal(2, capture.Spans.Count(span => span.Events.Any(item => item.Name == "rate_limit.acquisition")));
        Assert.All(capture.Spans, span => Assert.DoesNotContain("secret-result", string.Join(" ", span.TagObjects.Select(tag => tag.Value))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledTelemetryOrSamplingProducesNoSpans(bool sampleNone)
    {
        using var capture = new Capture(sampleNone ? ActivitySamplingResult.None : ActivitySamplingResult.AllDataAndRecorded);
        var builder = new ResiliencePipelineBuilder<int>().AddRetry(RetryOptions);
        if (sampleNone) builder.WithTelemetry(new("trace-test"));
        Assert.Equal(42, await builder.Build().ExecuteAsync(_ => Task.FromResult(42)));
        Assert.Empty(capture.Spans);
    }

    [Theory]
    [InlineData("sample")]
    [InlineData("start")]
    [InlineData("stop")]
    public async Task ThrowingListenersPreserveResultsErrorsAndAmbientParent(string failure)
    {
        using var parent = new Activity("incoming").Start();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ResilienceTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => failure == "sample" ? throw new InvalidOperationException() : ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ => { if (failure == "start") throw new InvalidOperationException(); },
            ActivityStopped = _ => { if (failure == "stop") throw new InvalidOperationException(); }
        };
        ActivitySource.AddActivityListener(listener);
        var pipeline = new ResiliencePipelineBuilder<int>().WithTelemetry(new("trace-test")).AddRetry(RetryOptions).Build();
        Assert.Equal(42, await pipeline.ExecuteAsync(_ => Task.FromResult(42)));
        Assert.Same(parent, Activity.Current);
        var original = new ArgumentException("secret");
        Assert.Same(original, await Assert.ThrowsAsync<ArgumentException>(() => pipeline.ExecuteAsync(_ => Task.FromException<int>(original))));
        Assert.Same(parent, Activity.Current);
    }

    [Fact]
    public async Task ConcurrentExecutionsKeepTheirOwnSpanHierarchy()
    {
        using var capture = new Capture();
        var pipeline = new ResiliencePipelineBuilder<int>().WithTelemetry(new("trace-test"))
            .AddRetry(RetryOptions).Build();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(value => pipeline.ExecuteAsync(async _ =>
        { await Task.Yield(); return value; })));
        var roots = capture.Spans.Where(span => span.OperationName == "resilience.pipeline").ToArray();
        Assert.Equal(20, roots.Length);
        Assert.Equal(20, roots.Select(span => span.SpanId).Distinct().Count());
        foreach (var root in roots)
        {
            var retry = Assert.Single(capture.Spans, span => span.ParentSpanId == root.SpanId);
            var attempt = Assert.Single(capture.Spans, span => span.ParentSpanId == retry.SpanId);
            Assert.Equal(root.TraceId, attempt.TraceId);
        }
    }

    [Fact]
    public async Task TelemetryDefaultsAreSnapshottedAndExplicitOverridesWin()
    {
        using var capture = new Capture();
        var builder = new ResiliencePipelineBuilder<int>().WithTelemetry(new("first"))
            .AddRetry(RetryOptions with { Telemetry = new("override") });
        var pipeline = builder.Build();
        builder.WithTelemetry(new("second"));
        await pipeline.ExecuteAsync(_ => Task.FromResult(42));
        Assert.Equal("first", Assert.Single(capture.Spans, span => span.OperationName == "resilience.pipeline").GetTagItem("dependency.name"));
        Assert.All(capture.Spans.Where(span => span.OperationName != "resilience.pipeline"), span => Assert.Equal("override", span.GetTagItem("dependency.name")));
    }
}
