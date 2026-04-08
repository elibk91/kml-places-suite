using Microsoft.Extensions.Logging;

namespace KmlSuite.Shared.Tracing;

public sealed class LoggerTraceInvocationRecorder : ITraceInvocationRecorder
{
    private readonly ILogger<LoggerTraceInvocationRecorder> _logger;

    public LoggerTraceInvocationRecorder(ILogger<LoggerTraceInvocationRecorder> logger)
    {
        _logger = logger;
    }

    public void Record(TraceInvocationEvent invocationEvent)
    {
        var logLevel = invocationEvent.Outcome.Equals("faulted", StringComparison.OrdinalIgnoreCase)
            ? LogLevel.Warning
            : LogLevel.Debug;

        _logger.Log(
            logLevel,
            "TRACE {ServiceType}.{MethodName} -> {ImplementationType} outcome={Outcome} durationMs={DurationMilliseconds} exceptionType={ExceptionType}",
            invocationEvent.ServiceType,
            invocationEvent.MethodName,
            invocationEvent.ImplementationType,
            invocationEvent.Outcome,
            invocationEvent.DurationMilliseconds,
            invocationEvent.ExceptionType ?? "none");
    }
}
