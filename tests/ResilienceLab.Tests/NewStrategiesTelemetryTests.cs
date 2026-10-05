using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;

namespace ResilienceLab.Tests;

public class NewStrategiesTelemetryTests
{
    [Fact]
    public async Task NewStrategiesEmitCountersAndWaitHistograms()
    {
        const string dependency = "new-strategies-telemetry";
        var values = new ConcurrentQueue<(string Name, double Value, string? Strategy, string? Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == ResilienceTelemetry.MeterName) owner.EnableMeasurementEvents(instrument);
        };
        void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? name = null, strategy = null, outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "dependency.name") name = tag.Value as string;
                if (tag.Key == "resilience.strategy") strategy = tag.Value as string;
                if (tag.Key == "rate_limit.outcome") outcome = tag.Value as string;
            }
            if (name == dependency) values.Enqueue((instrument.Name, value, strategy, outcome));
        }
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.Start();
        var telemetry = new ResilienceTelemetry(dependency);
        await Fallback.ExecuteAsync<int>(_ => throw new HttpRequestException(), (_, _) => Task.FromResult(1),
            new FallbackOptions<int> { ShouldHandleException = _ => true, Telemetry = telemetry });
        await Hedging.ExecuteAsync((_, _) => Task.FromResult(1), new HedgingOptions<int>
        { Delay = TimeSpan.FromSeconds(1), Telemetry = telemetry });
        var attempts = 0;
        await Retry.ExecuteAsync(_ => Task.FromResult(++attempts == 1 ? -1 : 1), new RetryOptions<int>
        { BaseDelay = TimeSpan.Zero, ShouldRetryResult = result => result < 0, Telemetry = telemetry });
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        var rate = new RateLimit(limiter, telemetry);
        using (var occupied = limiter.AttemptAcquire())
            await Assert.ThrowsAsync<RateLimitRejectedException>(() => rate.ExecuteAsync(_ => Task.FromResult(1)));
        await rate.ExecuteAsync(_ => Task.FromResult(1));

        Assert.Equal(1, values.Where(v => v.Name == "resilience.fallback.count").Sum(v => v.Value));
        Assert.Equal(1, values.Where(v => v.Name == "resilience.hedging.attempt.count").Sum(v => v.Value));
        Assert.Equal(1, values.Where(v => v.Name == "resilience.hedging.win.count").Sum(v => v.Value));
        Assert.Equal("result_retry", Assert.Single(values, v => v.Name == "resilience.retry.count").Strategy);
        var acquisitions = values.Where(v => v.Name == "resilience.rate_limit.acquisition.count").ToArray();
        Assert.Equal(new[] { "rejected", "acquired" }, acquisitions.Select(v => v.Outcome));
        Assert.Equal(2, values.Count(v => v.Name == "resilience.rate_limit.wait"));
        Assert.All(values.Where(v => v.Name == "resilience.rate_limit.wait"), v => Assert.True(v.Value >= 0));
    }
}
