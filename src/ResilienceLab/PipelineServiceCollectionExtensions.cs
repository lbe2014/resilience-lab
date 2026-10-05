using Microsoft.Extensions.DependencyInjection;

namespace ResilienceLab;

public static class PipelineServiceCollectionExtensions
{
    /// <summary>Registers a named singleton pipeline, sharing circuit state across resolutions.</summary>
    public static IServiceCollection AddResiliencePipeline<T>(this IServiceCollection services,
        string name, Action<ResiliencePipelineBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddResiliencePipeline<T>(name, (_, builder) => configure(builder));
    }

    public static IServiceCollection AddResiliencePipeline<T>(this IServiceCollection services,
        string name, Action<IServiceProvider, ResiliencePipelineBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        if (services.Any(descriptor => descriptor.IsKeyedService
            && descriptor.ServiceType == typeof(ResiliencePipeline<T>) && Equals(descriptor.ServiceKey, name)))
            throw new ArgumentException("A pipeline with this name and result type is already registered.", nameof(name));
        services.AddKeyedSingleton<ResiliencePipeline<T>>(name, (provider, _) =>
        {
            var builder = new ResiliencePipelineBuilder<T>();
            configure(provider, builder);
            return builder.Build();
        });
        return services;
    }
}
