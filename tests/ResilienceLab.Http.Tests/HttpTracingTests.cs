using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace ResilienceLab.Http.Tests;

public class HttpTracingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpAttemptsRecordStatusAndNeverRequestData(bool alwaysDown)
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ResilienceTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { if (Equals(activity.GetTagItem("dependency.name"), "http-trace-test")) spans.Enqueue(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        using var http = new HttpClient(new Handler(alwaysDown));
        using var resilient = new ResilientHttpClient(http, new HttpRetryOptions
        { MaxRetries = 1, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, Telemetry = new("http-trace-test") });
        using var response = await resilient.SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/private?token=secret");
            request.Headers.Add("Authorization", "Bearer secret");
            return request;
        });
        Assert.Equal(alwaysDown ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, response.StatusCode);
        var root = Assert.Single(spans, span => span.OperationName == "resilience.http_retry");
        var attempts = spans.Where(span => span.OperationName == "resilience.http_retry.attempt").ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal(new[] { 503, alwaysDown ? 503 : 200 }, attempts.Select(span => (int)span.GetTagItem("http.response.status_code")!).ToArray());
        Assert.Equal(ActivityStatusCode.Error, attempts[0].Status);
        Assert.Equal(alwaysDown ? ActivityStatusCode.Error : ActivityStatusCode.Unset, attempts[1].Status);
        Assert.Equal(alwaysDown ? ActivityStatusCode.Error : ActivityStatusCode.Unset, root.Status);
        Assert.All(attempts, span => Assert.Equal(root.SpanId, span.ParentSpanId));
        Assert.All(spans, span => Assert.DoesNotContain("secret", string.Join(" ", span.TagObjects.Select(tag => tag.Value))));
        Assert.Contains(root.Events, item => item.Name == "retry.scheduled");
        Assert.Equal("success", root.GetTagItem("resilience.outcome"));
    }

    private sealed class Handler(bool alwaysDown) : HttpMessageHandler
    {
        private int _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(++_calls == 1 || alwaysDown ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent("secret-body") });
    }
}
