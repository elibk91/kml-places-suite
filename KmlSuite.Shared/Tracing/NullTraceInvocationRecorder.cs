namespace KmlSuite.Shared.Tracing;
public sealed class NullTraceInvocationRecorder : ITraceInvocationRecorder
{
    public void Record(TraceInvocationEvent invocationEvent)
    {
    }
}
