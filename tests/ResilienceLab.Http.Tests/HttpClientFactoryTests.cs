using Microsoft.Extensions.DependencyInjection;

namespace ResilienceLab.Http.Tests;

public class HttpClientFactoryTests
{
    [Fact]
    public async Task TypedClientUsesFactoryConfigurationAndRetries()
    {
        var services = new ServiceCollection(); var calls = 0;
        services.AddResilientHttpClient(client => client.BaseAddress = new Uri("https://example.invalid/"),
            _ => new HttpRetryOptions { BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero })
            .ConfigurePrimaryHttpMessageHandler(() => new Handler(request =>
            {
                Assert.Equal("https://example.invalid/api", request.RequestUri!.AbsoluteUri);
                return new HttpResponseMessage(++calls == 1 ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK);
            }));
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IHttpClientFactory>());
        var typed = provider.GetRequiredService<ResilientHttpClient>();
        using var result = await typed.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "api"));
        Assert.Equal(2, calls); Assert.Equal(System.Net.HttpStatusCode.OK, result.StatusCode);
        Assert.NotSame(typed, provider.GetRequiredService<ResilientHttpClient>());
    }

    [Fact]
    public async Task OptionsCanUseServicesAndDisableRetries()
    {
        var services = new ServiceCollection(); var calls = 0;
        services.AddSingleton(new HttpRetryOptions { MaxRetries = 0 });
        services.AddResilientHttpClient(configureRetry: provider => provider.GetRequiredService<HttpRetryOptions>())
            .ConfigurePrimaryHttpMessageHandler(() => new Handler(_ =>
            { calls++; return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable); }));
        using var provider = services.BuildServiceProvider();
        using var result = await provider.GetRequiredService<ResilientHttpClient>().SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.invalid"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RegistrationValidatesServices() => Assert.Throws<ArgumentNullException>(() =>
        ServiceCollectionExtensions.AddResilientHttpClient(null!));

    [Fact]
    public async Task TypedClientDisposalDoesNotBreakOtherFactoryClients()
    {
        var services = new ServiceCollection();
        services.AddResilientHttpClient().ConfigurePrimaryHttpMessageHandler(() =>
            new Handler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)));
        using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<ResilientHttpClient>(); first.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.invalid")));
        using var second = provider.GetRequiredService<ResilientHttpClient>();
        using var response = await second.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://example.invalid"));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ManualWrapperDoesNotDisposeCallerOwnedClient()
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)));
        var wrapper = new ResilientHttpClient(client); wrapper.Dispose();
        using var response = await client.GetAsync("https://example.invalid");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
