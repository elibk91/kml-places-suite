namespace KmlSuite.Shared.Tracing;
public sealed class TraceInvocationEvent
{
    public required string ServiceType { get; init; }
    public required string ImplementationType { get; init; }
    public required string MethodName { get; init; }
    public required string Outcome { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public required long DurationMilliseconds { get; init; }
    public string? ExceptionType { get; init; }
}
