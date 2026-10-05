using System.Net;
using Microsoft.Extensions.Time.Testing;
using ResilienceLab.Http;

namespace ResilienceLab.Http.Tests;

public sealed class HttpRetryTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);
    private static readonly HttpRetryOptions Immediate = new()
    {
        BaseDelay = TimeSpan.Zero,
        MaxDelay = TimeSpan.Zero,
        UseJitter = false
    };

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task SelectedStatusRetriesUpToLimitAndReturnsFinalFailure(int status)
    {
        using var handler = new StubHandler((_, _, _) => Reply(status));
        using var client = new HttpClient(handler);
        var retry = new ResilientHttpClient(client, Immediate);

        using var response = await retry.SendAsync(Request);

        Assert.Equal(4, handler.Calls);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(501)]
    public async Task OtherStatusesAreReturnedWithoutRetries(int status)
    {
        using var handler = new StubHandler((_, _, _) => Reply(status));
        using var client = new HttpClient(handler);
        using var response = await new ResilientHttpClient(client, Immediate).SendAsync(Request);

        Assert.Equal(1, handler.Calls);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", false, 4)]
    [InlineData("HEAD", false, 4)]
    [InlineData("POST", false, 1)]
    [InlineData("PUT", false, 1)]
    [InlineData("PATCH", false, 1)]
    [InlineData("DELETE", false, 1)]
    [InlineData("POST", true, 4)]
    [InlineData("PUT", true, 4)]
    [InlineData("PATCH", true, 4)]
    [InlineData("DELETE", true, 4)]
    public async Task MethodsRequireExplicitIdempotenceOptIn(string method, bool optIn, int expected)
    {
        using var handler = new StubHandler((_, _, _) => Reply(503));
        using var client = new HttpClient(handler);
        var retry = new ResilientHttpClient(client, Immediate with
        {
            AllowRetryForIdempotentOperations = optIn
        });

        using var response = await retry.SendAsync(() => new HttpRequestMessage(new HttpMethod(method), "https://example.test"));

        Assert.Equal(expected, handler.Calls);
    }

    [Fact]
    public async Task CreatesNewRequestsAndReleasesDiscardedResourcesButKeepsFinalResponseAlive()
    {
        var requests = new List<HttpRequestMessage>();
        var requestContents = new List<TrackedContent>();
        var responses = new List<TrackedContent>();
        using var handler = new StubHandler((request, call, _) =>
        {
            requests.Add(request);
            var content = new TrackedContent();
            responses.Add(content);
            return Task.FromResult(new HttpResponseMessage(call == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = content
            });
        });
        using var client = new HttpClient(handler);
        var retry = new ResilientHttpClient(client, Immediate);
        var response = await retry.SendAsync(() =>
        {
            var content = new TrackedContent();
            requestContents.Add(content);
            return new HttpRequestMessage(HttpMethod.Get, "https://example.test") { Content = content };
        });

        Assert.Equal(2, handler.Calls);
        Assert.NotSame(requests[0], requests[1]);
        Assert.All(requestContents, content => Assert.True(content.Disposed));
        Assert.True(responses[0].Disposed);
        Assert.False(responses[1].Disposed);
        Assert.Equal("response", await response.Content.ReadAsStringAsync());
        response.Dispose();
        Assert.True(responses[1].Disposed);

        // El wrapper no adquiere la propiedad del cliente.
        using var anotherResponse = await client.GetAsync("https://example.test");
        Assert.Equal(HttpStatusCode.OK, anotherResponse.StatusCode);
    }

    [Fact]
    public async Task TransportFailureRetriesAndPreservesFinalException()
    {
        var failure = new HttpRequestException("transport failure");
        using var handler = new StubHandler((_, _, _) => Task.FromException<HttpResponseMessage>(failure));
        using var client = new HttpClient(handler);

        var actual = await Assert.ThrowsAsync<HttpRequestException>(() => new ResilientHttpClient(client, Immediate).SendAsync(Request));

        Assert.Same(failure, actual);
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task TransportFailureReleasesRequestBeforeBackoff()
    {
        var time = new FakeTimeProvider();
        var content = new TrackedContent();
        using var handler = new StubHandler((_, _, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException()));
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var retry = new ResilientHttpClient(client, new HttpRetryOptions { UseJitter = false }, Runtime(time));
        var task = retry.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://example.test") { Content = content }, cancellation.Token);

        Assert.False(task.IsCompleted);
        Assert.True(content.Disposed);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Guard));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ReturnsHeadersWithoutReadingBodyAndDoesNotRetryLaterBodyFailure()
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnreadableContent()
        }));
        using var client = new HttpClient(handler);
        using var response = await new ResilientHttpClient(client, Immediate).SendAsync(Request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => response.Content.ReadAsStringAsync());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("POST", false, 1)]
    [InlineData("POST", true, 4)]
    [InlineData("GET", false, 4)]
    public async Task TransportFailuresRespectMethodPolicy(string method, bool optIn, int expected)
    {
        using var handler = new StubHandler((_, _, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException()));
        using var client = new HttpClient(handler);
        var retry = new ResilientHttpClient(client, Immediate with { AllowRetryForIdempotentOperations = optIn });

        await Assert.ThrowsAsync<HttpRequestException>(() => retry.SendAsync(
            () => new HttpRequestMessage(new HttpMethod(method), "https://example.test")));

        Assert.Equal(expected, handler.Calls);
    }

    [Fact]
    public async Task ZeroRetriesSendsOnce()
    {
        using var handler = new StubHandler((_, _, _) => Reply(503));
        using var client = new HttpClient(handler);
        using var response = await new ResilientHttpClient(client, Immediate with { MaxRetries = 0 }).SendAsync(Request);

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UnselectedExceptionIsNotRetried()
    {
        var failure = new InvalidOperationException("handler bug");
        using var handler = new StubHandler((_, _, _) => Task.FromException<HttpResponseMessage>(failure));
        using var client = new HttpClient(handler);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => new ResilientHttpClient(client, Immediate).SendAsync(Request));

        Assert.Same(failure, actual);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OperationCancellationIsNeverRetried()
    {
        using var handler = new StubHandler((_, _, _) => Task.FromException<HttpResponseMessage>(new OperationCanceledException()));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ResilientHttpClient(client, Immediate).SendAsync(Request));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AlreadyCancelledTokenDoesNotCreateRequest()
    {
        using var client = new HttpClient(new StubHandler((_, _, _) => Reply(200)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factoryCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ResilientHttpClient(client).SendAsync(() =>
        {
            factoryCalls++;
            return Request();
        }, cancellation.Token));

        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task CancellationDuringSendReachesHandlerWithoutRetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler(async (_, _, ct) =>
        {
            entered.SetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var task = new ResilientHttpClient(client, Immediate).SendAsync(Request, cancellation.Token);
        await entered.Task.WaitAsync(Guard);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Guard));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task HandlerThatReturnsAfterCallerCancellationStillReleasesResponseAndRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var requestContent = new TrackedContent();
        var responseContent = new TrackedContent();
        using var handler = new StubHandler((_, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = responseContent });
        });
        using var client = new HttpClient(handler);
        var retry = new ResilientHttpClient(client, Immediate);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test") { Content = requestContent },
            cancellation.Token));

        Assert.True(requestContent.Disposed);
        Assert.True(responseContent.Disposed);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CancellationDuringBackoffStopsRetryAndResourcesAreAlreadyReleased()
    {
        var time = new FakeTimeProvider();
        var responseContent = new TrackedContent();
        var requestContent = new TrackedContent();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = responseContent
        }));
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var retry = new ResilientHttpClient(client, new HttpRetryOptions { UseJitter = false }, Runtime(time));
        var task = retry.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://example.test") { Content = requestContent }, cancellation.Token);

        Assert.False(task.IsCompleted);
        Assert.True(responseContent.Disposed);
        Assert.True(requestContent.Disposed);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Guard));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(null, 1000)]
    [InlineData("0", 1000)]
    [InlineData("3", 3000)]
    [InlineData("60", 5000)]
    [InlineData("invalid", 1000)]
    [InlineData("-1", 1000)]
    [InlineData("Fri, 02 Jan 2026 00:00:03 GMT", 3000)]
    [InlineData("Fri, 02 Jan 2026 00:00:30 GMT", 5000)]
    [InlineData("Thu, 01 Jan 2026 00:00:00 GMT", 1000)]
    public async Task RetryAfterUsesLargestDelayWithCapAndIgnoresInvalidOrPastValues(string? header, int expectedMilliseconds)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        using var handler = new StubHandler((_, call, _) =>
        {
            var response = new HttpResponseMessage(call == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            if (header is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", header);
            }
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var retry = new ResilientHttpClient(client, new HttpRetryOptions
        {
            MaxRetries = 1,
            BaseDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(5),
            UseJitter = false
        }, Runtime(time));
        var task = retry.SendAsync(Request);
        Assert.Equal(1, handler.Calls);
        Assert.False(task.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(expectedMilliseconds) - TimeSpan.FromTicks(1));
        Assert.Equal(1, handler.Calls);
        time.Advance(TimeSpan.FromTicks(1));
        using var final = await task.WaitAsync(Guard);

        Assert.Equal(2, handler.Calls);
        Assert.Equal(HttpStatusCode.OK, final.StatusCode);
    }

    [Fact]
    public async Task JitterIsAppliedToBackoffUsingControlledRandomSource()
    {
        var time = new FakeTimeProvider();
        using var handler = new StubHandler((_, call, _) => Reply(call == 1 ? 503 : 200));
        using var client = new HttpClient(handler);
        var retry = new ResilientHttpClient(client, new HttpRetryOptions
        {
            BaseDelay = TimeSpan.FromSeconds(4),
            MaxDelay = TimeSpan.FromSeconds(5)
        }, new ResilienceRuntime(time, () => 0.25));
        var task = retry.SendAsync(Request);

        time.Advance(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));
        Assert.Equal(1, handler.Calls);
        time.Advance(TimeSpan.FromTicks(1));
        using var response = await task.WaitAsync(Guard);

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task FactoryFailuresAreNotRetriedEvenWhenTheyAreTransportExceptions()
    {
        var calls = 0;
        using var handler = new StubHandler((_, _, _) => Reply(200));
        using var client = new HttpClient(handler);
        var error = new HttpRequestException("factory error");

        var actual = await Assert.ThrowsAsync<HttpRequestException>(() => new ResilientHttpClient(client, Immediate).SendAsync(() =>
        {
            calls++;
            throw error;
        }));

        Assert.Same(error, actual);
        Assert.Equal(1, calls);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task NullFactoryAndNullResultAreRejected()
    {
        using var client = new HttpClient(new StubHandler((_, _, _) => Reply(200)));
        var retry = new ResilientHttpClient(client);

        await Assert.ThrowsAsync<ArgumentNullException>(() => retry.SendAsync(null!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => retry.SendAsync(() => null!));
    }

    [Fact]
    public async Task ReusedRequestIsRejectedWithoutSendingTwice()
    {
        using var handler = new StubHandler((_, _, _) => Reply(503));
        using var client = new HttpClient(handler);
        var request = Request();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new ResilientHttpClient(client, Immediate).SendAsync(() => request));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void InvalidOptionsAreRejectedBeforeSending()
    {
        using var client = new HttpClient();
        Assert.Throws<ArgumentNullException>(() => new ResilientHttpClient(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResilientHttpClient(client, Immediate with { MaxRetries = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResilientHttpClient(client, Immediate with { BaseDelay = TimeSpan.FromTicks(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResilientHttpClient(client, Immediate with { BaseDelay = TimeSpan.FromSeconds(2), MaxDelay = TimeSpan.FromSeconds(1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResilientHttpClient(client, Immediate with { MaxDelay = TimeSpan.MaxValue }));
    }

    private static HttpRequestMessage Request() => new(HttpMethod.Get, "https://example.test");
    private static Task<HttpResponseMessage> Reply(int status) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
    private static ResilienceRuntime Runtime(TimeProvider time) => new(time, () => throw new InvalidOperationException("Jitter desactivado."));

    private sealed class StubHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, ++Calls, cancellationToken);
    }

    private sealed class TrackedContent() : StringContent("response")
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class UnreadableContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.FromException(new HttpRequestException("body disconnected"));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
