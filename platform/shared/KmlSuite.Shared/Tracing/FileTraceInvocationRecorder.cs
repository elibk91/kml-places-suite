using System.Text.Json;
namespace KmlSuite.Shared.Tracing;
public sealed class FileTraceInvocationRecorder : ITraceInvocationRecorder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };
    private readonly string _outputPath;
    private readonly Lock _syncRoot = new();
    public FileTraceInvocationRecorder(string outputPath)
    {
        _outputPath = outputPath;
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
    public void Record(TraceInvocationEvent invocationEvent)
    {
        var line = JsonSerializer.Serialize(invocationEvent, JsonOptions);
        lock (_syncRoot)
        {
            File.AppendAllText(_outputPath, line + Environment.NewLine);
        }
    }
}
