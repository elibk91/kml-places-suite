using System.Text.Json;
using KmlGenerator.Core.Models;
using KmlGenerator.Core.Services;

return await KmlConsoleRunner.RunAsync(args, Console.Out, Console.Error);

/// <summary>
/// Console signpost: this host only handles file I/O and delegates the actual KML work to the shared service.
/// </summary>
public static class KmlConsoleRunner
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var parsed = ParseArguments(args);
        if (parsed is null)
        {
            await error.WriteLineAsync("Usage: kml-console --input request.json --output outline.kml");
            return 1;
        }

        try
        {
            var requestText = await File.ReadAllTextAsync(parsed.Value.InputPath);
            var request = JsonSerializer.Deserialize<GenerateKmlRequest>(requestText, JsonOptions);

            if (request is null)
            {
                await error.WriteLineAsync("The request file did not contain a valid JSON payload.");
                return 1;
            }

            var service = new KmlGenerationService();
            var result = service.Generate(request);

            await File.WriteAllTextAsync(parsed.Value.OutputPath, result.Kml);
            await output.WriteLineAsync($"Saved {result.BoundaryPointCount} outline dots to {parsed.Value.OutputPath}");
            return 0;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static ConsoleArguments? ParseArguments(IReadOnlyList<string> args)
    {
        string? inputPath = null;
        string? outputPath = null;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--input" when index + 1 < args.Count:
                    inputPath = args[++index];
                    break;
                case "--output" when index + 1 < args.Count:
                    outputPath = args[++index];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(inputPath) || string.IsNullOrWhiteSpace(outputPath))
        {
            return null;
        }

        return new ConsoleArguments(inputPath, outputPath);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly record struct ConsoleArguments(string InputPath, string OutputPath);
}
