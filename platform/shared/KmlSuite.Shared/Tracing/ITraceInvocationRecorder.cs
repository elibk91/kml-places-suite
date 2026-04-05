namespace KmlSuite.Shared.Tracing;
public interface ITraceInvocationRecorder
{
    void Record(TraceInvocationEvent invocationEvent);
}
