namespace KmlSuite.Shared.Tracing;
public static class TraceEnvironment
{
    public const string EventsPathVariableName = "KMLSUITE_TRACE_EVENTS_PATH";
    public static string? GetEventsPath() =>
        Environment.GetEnvironmentVariable(EventsPathVariableName);
}
