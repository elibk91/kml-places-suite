using KmlSuite.Shared.Tracing;
using Microsoft.Extensions.Logging;
using PlacesGatherer.Console.Models;
namespace PlacesGatherer.Console.Secrets;
public sealed class SecretProviderFactory : ISecretProviderFactory
{
    private readonly ILogger<SecretProviderFactory> _logger;
    private readonly ITraceProxyFactory _traceProxyFactory;
    private readonly ILogger<LocalConfigurationSecretProvider> _localProviderLogger;
    public SecretProviderFactory(
        ILogger<SecretProviderFactory> logger,
        ITraceProxyFactory traceProxyFactory,
        ILogger<LocalConfigurationSecretProvider> localProviderLogger)
    {
        _logger = logger;
        _traceProxyFactory = traceProxyFactory;
        _localProviderLogger = localProviderLogger;
    }
    public ISecretProvider Create(SecretSettings settings)
    {
        if (settings.Provider.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            var provider = new LocalConfigurationSecretProvider(settings, _localProviderLogger);
            return _traceProxyFactory.Create<ISecretProvider>(provider);
        }
        throw new NotSupportedException(
            $"Secret provider '{settings.Provider}' is not implemented yet. Use 'Local' for now.");
    }
}
