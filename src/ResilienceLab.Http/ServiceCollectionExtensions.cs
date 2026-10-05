using Microsoft.Extensions.DependencyInjection;

namespace ResilienceLab.Http;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers a transient typed client backed by IHttpClientFactory.</summary>
    public static IHttpClientBuilder AddResilientHttpClient(this IServiceCollection services,
        Action<HttpClient>? configureClient = null,
        Func<IServiceProvider, HttpRetryOptions>? configureRetry = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddHttpClient(nameof(ResilientHttpClient), configureClient ?? (_ => { }))
            .AddTypedClient<ResilientHttpClient>((client, provider) =>
                new ResilientHttpClient(client, configureRetry?.Invoke(provider), ResilienceRuntime.System, ownsClient: true));
    }
}
