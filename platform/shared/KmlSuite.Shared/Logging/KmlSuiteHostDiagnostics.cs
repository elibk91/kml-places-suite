using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KmlSuite.Shared.Logging;

public static class KmlSuiteHostDiagnostics
{
    public const string LogDirectoryVariableName = "KMLSUITE_LOG_DIRECTORY";

    public static IServiceCollection AddKmlSuiteHostDiagnostics(this IServiceCollection services, string hostName)
    {
        var diagnosticsPaths = CreatePaths(hostName);

        services.AddLogging(builder =>
        {
            builder.AddSimpleConsole(console =>
            {
                console.TimestampFormat = "HH:mm:ss.fff ";
                console.SingleLine = true;
            });
            builder.AddProvider(new DatedTextFileLoggerProvider(diagnosticsPaths.LogPath));
            builder.SetMinimumLevel(LogLevel.Trace);
        });

        return services;
    }

    public static string ResolveLogDirectory()
    {
        var configuredPath = Environment.GetEnvironmentVariable(LogDirectoryVariableName);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        var repoRoot = TryFindRepoRoot(Directory.GetCurrentDirectory())
            ?? TryFindRepoRoot(AppContext.BaseDirectory);

        return repoRoot is null
            ? Path.Combine(Directory.GetCurrentDirectory(), "logs")
            : Path.Combine(repoRoot, "workflow", "out", "diagnostics", "logs");
    }

    private static DiagnosticPaths CreatePaths(string hostName)
    {
        var sanitizedHostName = SanitizeFileName(hostName);
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH-mm-ss");
        var logDirectory = ResolveLogDirectory();

        Directory.CreateDirectory(logDirectory);

        return new DiagnosticPaths(
            Path.Combine(logDirectory, $"{timestamp}-{sanitizedHostName}.txt"));
    }

    private static string? TryFindRepoRoot(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            return null;
        }

        var directoryInfo = new DirectoryInfo(startPath);
        if (!directoryInfo.Exists && directoryInfo.Parent is not null)
        {
            directoryInfo = directoryInfo.Parent;
        }

        while (directoryInfo is not null)
        {
            if (File.Exists(Path.Combine(directoryInfo.FullName, "KmlSuite.slnx")))
            {
                return directoryInfo.FullName;
            }

            directoryInfo = directoryInfo.Parent;
        }

        return null;
    }

    private static string SanitizeFileName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(character) ? '-' : character);
        }

        return builder.ToString();
    }

    private sealed record DiagnosticPaths(string LogPath);
}

internal sealed class DatedTextFileLoggerProvider : ILoggerProvider
{
    private readonly string _logPath;
    private readonly Lock _syncRoot = new();

    public DatedTextFileLoggerProvider(string logPath)
    {
        _logPath = logPath;
        var directory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public ILogger CreateLogger(string categoryName) =>
        new DatedTextFileLogger(categoryName, _logPath, _syncRoot);

    public void Dispose()
    {
    }

    private sealed class DatedTextFileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly string _logPath;
        private readonly Lock _syncRoot;

        public DatedTextFileLogger(string categoryName, string logPath, Lock syncRoot)
        {
            _categoryName = categoryName;
            _logPath = logPath;
            _syncRoot = syncRoot;
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
            {
                return;
            }

            var line = BuildLogLine(logLevel, eventId, message, exception);
            lock (_syncRoot)
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }

        private string BuildLogLine(LogLevel logLevel, EventId eventId, string message, Exception? exception)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
            builder.Append("] [");
            builder.Append(logLevel.ToString().ToUpperInvariant());
            builder.Append("] ");
            builder.Append(_categoryName);

            if (eventId.Id != 0 || !string.IsNullOrWhiteSpace(eventId.Name))
            {
                builder.Append(" (");
                builder.Append(eventId.Id);
                if (!string.IsNullOrWhiteSpace(eventId.Name))
                {
                    builder.Append(':');
                    builder.Append(eventId.Name);
                }

                builder.Append(')');
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                builder.Append(' ');
                builder.Append(message.Replace(Environment.NewLine, " | ", StringComparison.Ordinal));
            }

            if (exception is not null)
            {
                builder.Append(" Exception=");
                builder.Append(exception);
            }

            return builder.ToString();
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
