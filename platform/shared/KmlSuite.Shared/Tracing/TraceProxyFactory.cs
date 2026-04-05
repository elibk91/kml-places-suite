using Microsoft.Extensions.Logging;
namespace KmlSuite.Shared.Tracing;
public sealed class TraceProxyFactory : ITraceProxyFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITraceInvocationRecorder _recorder;
    public TraceProxyFactory(ILoggerFactory loggerFactory, ITraceInvocationRecorder recorder)
    {
        _loggerFactory = loggerFactory;
        _recorder = recorder;
    }
    public TService Create<TService>(TService target)
        where TService : class
    {
        return DispatchTraceProxy<TService>.Create(target, _loggerFactory, _recorder);
    }
}
