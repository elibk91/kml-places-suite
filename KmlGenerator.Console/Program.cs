using System.Text.Json;
using System.Globalization;
using KmlGenerator.Core.Models;
using KmlGenerator.Core.Services;
using KmlSuite.Shared.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
return await KmlConsoleProgram.RunAsync(args, Console.Out, Console.Error);
public static class KmlConsoleProgram
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddSimpleConsole(console =>
            {
                console.TimestampFormat = "HH:mm:ss.fff ";
                console.SingleLine = true;
            });
            builder.SetMinimumLevel(LogLevel.Trace);
        });
        services.AddKmlSuiteTracing();
        services.AddTracedSingleton<IKmlGenerationService, KmlGenerationService>();
        services.AddTracedSingleton<IKmlConsoleApp, KmlConsoleRunner>();
        await using var serviceProvider = services.BuildServiceProvider();
        return await serviceProvider.GetRequiredService<IKmlConsoleApp>().RunAsync(args, output, error);
    }
}
public sealed class KmlConsoleRunner : IKmlConsoleApp
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private readonly ILogger<KmlConsoleRunner> _logger;
    private readonly IKmlGenerationService _service;
    public KmlConsoleRunner(ILogger<KmlConsoleRunner> logger, IKmlGenerationService service)
    {
        _logger = logger;
        _service = service;
    }
    public async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var parsed = ParseArguments(args);
        if (parsed is null)
        {
            await error.WriteLineAsync("Usage: kml-console --input request.json [--output outline.kml] [--diagnose-latitude 33.7 --diagnose-longitude -84.3 [--diagnose-radius-miles 0.5] [--diagnose-top-per-category 5]]");
            return 1;
        }
        try
        {
            await using var requestStream = File.OpenRead(parsed.Value.InputPath);
            var request = await JsonSerializer.DeserializeAsync<GenerateKmlRequest>(requestStream, JsonOptions);
            _logger.LogInformation("Loaded KML request from {InputPath}", parsed.Value.InputPath);
            if (request is null)
            {
                await error.WriteLineAsync("The request file did not contain a valid JSON payload.");
                return 1;
            }

            if (parsed.Value.Diagnostic is not null)
            {
                var diagnosticArguments = parsed.Value.Diagnostic;
                var diagnostic = _service.DiagnoseCoverage(
                    request,
                    diagnosticArguments.Latitude,
                    diagnosticArguments.Longitude,
                    diagnosticArguments.RadiusMiles ?? request.RadiusMiles,
                    diagnosticArguments.TopPerCategory ?? 5);
                await WriteCoverageDiagnosticAsync(output, diagnostic);
            }

            if (!string.IsNullOrWhiteSpace(parsed.Value.OutputPath))
            {
                var result = _service.Generate(request);
                await File.WriteAllTextAsync(parsed.Value.OutputPath, result.Kml);
                _logger.LogInformation("Wrote KML output to {OutputPath} with {BoundaryPointCount} emitted overlap points", parsed.Value.OutputPath, result.BoundaryPointCount);
                await output.WriteLineAsync($"Saved {result.BoundaryPointCount} overlap points to {parsed.Value.OutputPath}");
            }

            return 0;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "KML console runner failed");
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }
    private static async Task WriteCoverageDiagnosticAsync(TextWriter output, CoverageDiagnosticResult diagnostic)
    {
        await output.WriteLineAsync($"Coordinate: {diagnostic.Latitude}, {diagnostic.Longitude}");
        await output.WriteLineAsync($"RadiusMiles: {diagnostic.RadiusMiles}");
        await output.WriteLineAsync(string.Empty);

        foreach (var category in diagnostic.Categories)
        {
            await output.WriteLineAsync($"[{category.Category}]");
            foreach (var location in category.NearestLocations)
            {
                await output.WriteLineAsync(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{location.Label} | {location.DistanceMiles:N3} mi | {location.Latitude} | {location.Longitude}"));
            }

            await output.WriteLineAsync(string.Empty);
        }

        if (diagnostic.MissingCategories.Count == 0)
        {
            await output.WriteLineAsync("All categories have at least one point within radius.");
        }
        else
        {
            await output.WriteLineAsync($"Missing within radius: {string.Join(", ", diagnostic.MissingCategories)}");
        }
    }

    private ConsoleArguments? ParseArguments(IReadOnlyList<string> args)
    {
        string? inputPath = null;
        string? outputPath = null;
        double? diagnoseLatitude = null;
        double? diagnoseLongitude = null;
        double? diagnoseRadiusMiles = null;
        int? diagnoseTopPerCategory = null;

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
                case "--diagnose-latitude" when index + 1 < args.Count:
                    diagnoseLatitude = double.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--diagnose-longitude" when index + 1 < args.Count:
                    diagnoseLongitude = double.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--diagnose-radius-miles" when index + 1 < args.Count:
                    diagnoseRadiusMiles = double.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--diagnose-top-per-category" when index + 1 < args.Count:
                    diagnoseTopPerCategory = int.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
            }
        }

        CoverageDiagnosticArguments? diagnostic = diagnoseLatitude.HasValue && diagnoseLongitude.HasValue
            ? new CoverageDiagnosticArguments(diagnoseLatitude.Value, diagnoseLongitude.Value, diagnoseRadiusMiles, diagnoseTopPerCategory)
            : null;

        if (string.IsNullOrWhiteSpace(inputPath) || (string.IsNullOrWhiteSpace(outputPath) && diagnostic is null))
        {
            return null;
        }

        return new ConsoleArguments(inputPath, outputPath, diagnostic);
    }
    private readonly record struct ConsoleArguments(string InputPath, string? OutputPath, CoverageDiagnosticArguments? Diagnostic);

    private sealed record CoverageDiagnosticArguments(double Latitude, double Longitude, double? RadiusMiles, int? TopPerCategory);
}
