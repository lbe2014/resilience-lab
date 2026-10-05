using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace ResilienceLab.Http.Tests;

public class HttpObservabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordsRetriesForResponsesAndTransportErrors(bool transportError)
    {
        var name = transportError ? "http-transport-metrics" : "http-response-metrics";
        var measurements = new ConcurrentQueue<(string Name, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == ResilienceTelemetry.MeterName) owner.EnableMeasurementEvents(instrument);
        };
        void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags) copy[tag.Key] = tag.Value;
            if (Equals(copy.GetValueOrDefault("dependency.name"), name)) measurements.Enqueue((instrument.Name, value, copy));
        }
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.Start();

        var attempts = 0;
        using var client = new HttpClient(new Handler(() =>
        {
            attempts++;
            if (attempts == 1)
            {
                if (transportError) throw new HttpRequestException("SENSITIVE response URL");
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }));
        var resilient = new ResilientHttpClient(client, new HttpRetryOptions
        {
            BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero,
            Telemetry = new ResilienceTelemetry(name)
        });
        using var response = await resilient.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://example.invalid"));
        Assert.Equal(2, attempts);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var retry = Assert.Single(measurements, m => m.Name == "resilience.retry.count");
        Assert.Equal(1, retry.Value);
        Assert.Equal("http_retry", retry.Tags["resilience.strategy"]);
        if (transportError) Assert.Equal(typeof(HttpRequestException).FullName, retry.Tags["error.type"]);
        else Assert.Equal(503, retry.Tags["http.response.status_code"]);
        Assert.Equal(0, Assert.Single(measurements, m => m.Name == "resilience.retry.delay").Value);
        Assert.DoesNotContain(retry.Tags.Keys, key => key.Contains("url") || key.Contains("message"));
    }

    private sealed class Handler(Func<HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send());
    }
}
