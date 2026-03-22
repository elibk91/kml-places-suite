using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
namespace KmlSuite.Shared.Tracing;
public class DispatchTraceProxy<TService> : DispatchProxy
    where TService : class
{
    private TService? _target;
    private ILogger? _logger;
    private ITraceInvocationRecorder? _recorder;
    public static TService Create(TService target, ILoggerFactory loggerFactory, ITraceInvocationRecorder recorder)
    {
        var proxy = Create<TService, DispatchTraceProxy<TService>>();
        var tracingProxy = (DispatchTraceProxy<TService>)(object)proxy;
        tracingProxy._target = target;
        tracingProxy._logger = loggerFactory.CreateLogger($"TraceProxy.{typeof(TService).FullName}");
        tracingProxy._recorder = recorder;
        return (TService)proxy;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        ArgumentNullException.ThrowIfNull(_target);
        ArgumentNullException.ThrowIfNull(_logger);
        ArgumentNullException.ThrowIfNull(_recorder);
        var target = _target;
        var logger = _logger;
        var recorder = _recorder;
        var serviceType = typeof(TService).FullName ?? typeof(TService).Name;
        var implementationType = target.GetType().FullName ?? target.GetType().Name;
        var methodName = targetMethod.Name;
        var started = Stopwatch.StartNew();
        logger.LogTrace("Entering {ServiceType}.{MethodName} -> {ImplementationType}", serviceType, methodName, implementationType);
        try
        {
            var result = targetMethod.Invoke(target, args);
            if (result is Task taskResult)
            {
                if (targetMethod.ReturnType == typeof(Task))
                {
                    return InterceptTaskAsync(taskResult, logger, recorder, serviceType, implementationType, methodName, started);
                }

                if (targetMethod.ReturnType.IsGenericType
                    && targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    return InterceptGenericTaskAsync((dynamic)result, logger, recorder, serviceType, implementationType, methodName, started);
                }
            }
            RecordOutcome(recorder, serviceType, implementationType, methodName, "completed", started.ElapsedMilliseconds, null);
            logger.LogTrace("Leaving {ServiceType}.{MethodName} after {ElapsedMilliseconds} ms", serviceType, methodName, started.ElapsedMilliseconds);
            return result;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            RecordOutcome(recorder, serviceType, implementationType, methodName, "faulted", started.ElapsedMilliseconds, exception.InnerException.GetType().FullName);
            logger.LogError(exception.InnerException, "{ServiceType}.{MethodName} failed after {ElapsedMilliseconds} ms", serviceType, methodName, started.ElapsedMilliseconds);
            throw exception.InnerException;
        }
    }
    private async Task InterceptTaskAsync(Task task, ILogger logger, ITraceInvocationRecorder recorder, string serviceType, string implementationType, string methodName, Stopwatch started)
    {
        try
        {
            await task.ConfigureAwait(false);
            RecordOutcome(recorder, serviceType, implementationType, methodName, "completed", started.ElapsedMilliseconds, null);
            logger.LogTrace("Leaving {ServiceType}.{MethodName} after {ElapsedMilliseconds} ms", serviceType, methodName, started.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            RecordOutcome(recorder, serviceType, implementationType, methodName, "faulted", started.ElapsedMilliseconds, exception.GetType().FullName);
            logger.LogError(exception, "{ServiceType}.{MethodName} failed after {ElapsedMilliseconds} ms", serviceType, methodName, started.ElapsedMilliseconds);
            throw;
        }
    }
    private async Task<TResult> InterceptGenericTaskAsync<TResult>(Task<TResult> task, ILogger logger, ITraceInvocationRecorder recorder, string serviceType, string implementationType, string methodName, Stopwatch started)
    {
        try
        {
            var result = await task.ConfigureAwait(false);
            RecordOutcome(recorder, serviceType, implementationType, methodName, "completed", started.ElapsedMilliseconds, null);
            logger.LogTrace("Leaving {ServiceType}.{MethodName} after {ElapsedMilliseconds} ms", serviceType, methodName, started.ElapsedMilliseconds);
            return result;
        }
        catch (Exception exception)
        {
            RecordOutcome(recorder, serviceType, implementationType, methodName, "faulted", started.ElapsedMilliseconds, exception.GetType().FullName);
            logger.LogError(exception, "{ServiceType}.{MethodName} failed after {ElapsedMilliseconds} ms", serviceType, methodName, started.ElapsedMilliseconds);
            throw;
        }
    }
    private static void RecordOutcome(ITraceInvocationRecorder recorder, string serviceType, string implementationType, string methodName, string outcome, long durationMilliseconds, string? exceptionType)
    {
        recorder.Record(new TraceInvocationEvent
        {
            ServiceType = serviceType,
            ImplementationType = implementationType,
            MethodName = methodName,
            Outcome = outcome,
            TimestampUtc = DateTimeOffset.UtcNow,
            DurationMilliseconds = durationMilliseconds,
            ExceptionType = exceptionType
        });
    }
}
