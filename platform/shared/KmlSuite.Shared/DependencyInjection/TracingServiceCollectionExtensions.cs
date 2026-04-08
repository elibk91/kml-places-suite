using KmlSuite.Shared.Tracing;
using Microsoft.Extensions.DependencyInjection;

namespace KmlSuite.Shared.DependencyInjection;

public static class TracingServiceCollectionExtensions
{
    public static IServiceCollection AddKmlSuiteTracing(this IServiceCollection services)
    {
        services.AddSingleton<ITraceInvocationRecorder, LoggerTraceInvocationRecorder>();
        services.AddSingleton<ITraceProxyFactory, TraceProxyFactory>();
        return services;
    }
    public static IServiceCollection AddTracedSingleton<TService, TImplementation>(this IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        if (!typeof(TService).IsInterface)
        {
            throw new InvalidOperationException(
                $"AddTracedSingleton requires an interface service type. '{typeof(TService).FullName}' is not an interface.");
        }

        services.AddSingleton<TImplementation>();
        services.AddSingleton<TService>(static serviceProvider =>
            serviceProvider.GetRequiredService<ITraceProxyFactory>().Create<TService>(serviceProvider.GetRequiredService<TImplementation>()));
        return services;
    }
}
