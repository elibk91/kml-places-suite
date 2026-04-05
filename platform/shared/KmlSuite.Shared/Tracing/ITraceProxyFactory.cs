namespace KmlSuite.Shared.Tracing;
public interface ITraceProxyFactory
{
    TService Create<TService>(TService target)
        where TService : class;
}
