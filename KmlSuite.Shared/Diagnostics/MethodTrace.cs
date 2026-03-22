using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace KmlSuite.Shared.Diagnostics;

public static class MethodTrace
{
    public static IDisposable Enter(
        ILogger logger,
        string owner,
        IReadOnlyDictionary<string, object?>? arguments = null,
        [CallerMemberName] string memberName = "")
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            if (arguments is null || arguments.Count == 0)
            {
                logger.LogTrace("Entering {Owner}.{Member}", owner, memberName);
            }
            else
            {
                logger.LogTrace("Entering {Owner}.{Member} with {@Arguments}", owner, memberName, arguments);
            }
        }

        return new MethodTraceScope(logger, owner, memberName);
    }

    private sealed class MethodTraceScope : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _owner;
        private readonly string _memberName;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public MethodTraceScope(ILogger logger, string owner, string memberName)
        {
            _logger = logger;
            _owner = owner;
            _memberName = memberName;
        }

        public void Dispose()
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace(
                    "Leaving {Owner}.{Member} after {ElapsedMilliseconds} ms",
                    _owner,
                    _memberName,
                    _stopwatch.ElapsedMilliseconds);
            }
        }
    }
}
