using System.Text.Json;
using KmlGenerator.Core.Models;
using KmlSuite.Shared.DependencyInjection;
using KmlSuite.Shared.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LocationAssembler.Console;
using PlacesGatherer.Console.Models;

return await LocationAssemblerProgram.RunAsync(args, Console.Out, Console.Error);

public static class LocationAssemblerProgram
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
        services.AddTracedSingleton<ILocationAssemblerApp, LocationAssemblerRunner>();

        await using var serviceProvider = services.BuildServiceProvider();
        return await serviceProvider.GetRequiredService<ILocationAssemblerApp>().RunAsync(args, output, error);
    }
}

/// <summary>
/// Console signpost: this host converts normalized gathered points into the flat KML request contract.
/// </summary>
public sealed class LocationAssemblerRunner : ILocationAssemblerApp
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ILogger<LocationAssemblerRunner> _logger;

    public LocationAssemblerRunner(ILogger<LocationAssemblerRunner> logger)
    {
        _logger = logger;
    }

    public async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(LocationAssemblerRunner), new Dictionary<string, object?> { ["ArgumentCount"] = args.Length });
        var parsed = ParseArguments(args);
        if (parsed is null)
        {
            await error.WriteLineAsync("Usage: location-assembler --input places-1.jsonl --input places-2.jsonl --output request.json");
            return 1;
        }

        try
        {
            var records = new List<NormalizedPlaceRecord>();

            foreach (var inputPath in parsed.Value.InputPaths)
            {
                var fileRecordCount = 0;
                foreach (var line in await File.ReadAllLinesAsync(inputPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var record = JsonSerializer.Deserialize<NormalizedPlaceRecord>(line, JsonOptions);
                    if (record is null)
                    {
                        throw new InvalidOperationException("The jsonl input contains an invalid place record.");
                    }

                    records.Add(record);
                    fileRecordCount++;
                }
                _logger.LogInformation("Read {FileRecordCount} normalized records from {InputPath}", fileRecordCount, inputPath);
            }

            var locations = records
                .Select(record => new LocationInput
                {
                    Latitude = record.Latitude,
                    Longitude = record.Longitude,
                    Category = record.Category,
                    Label = record.Name
                })
                .Distinct(LocationInputComparer.Instance)
                .ToArray();

            _logger.LogInformation("Deduplicated {InputRecordCount} records into {UniqueLocationCount} unique locations", records.Count, locations.Length);

            var request = new GenerateKmlRequest
            {
                Locations = locations
            };

            await File.WriteAllTextAsync(parsed.Value.OutputPath, JsonSerializer.Serialize(request, JsonOptions));
            _logger.LogInformation("Assembled {RecordCount} records into {LocationCount} unique locations at {OutputPath}", records.Count, locations.Length, parsed.Value.OutputPath);
            await output.WriteLineAsync($"Saved {locations.Length} unique points to {parsed.Value.OutputPath}");
            return 0;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Location assembler failed");
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private (IReadOnlyList<string> InputPaths, string OutputPath)? ParseArguments(IReadOnlyList<string> args)
    {
        using var _ = MethodTrace.Enter(_logger, nameof(LocationAssemblerRunner));
        var inputPaths = new List<string>();
        string? outputPath = null;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--input" when index + 1 < args.Count:
                    inputPaths.Add(args[++index]);
                    break;
                case "--output" when index + 1 < args.Count:
                    outputPath = args[++index];
                    break;
            }
        }

        return inputPaths.Count == 0 || string.IsNullOrWhiteSpace(outputPath)
            ? null
            : (inputPaths, outputPath);
    }

    private sealed class LocationInputComparer : IEqualityComparer<LocationInput>
    {
        public static LocationInputComparer Instance { get; } = new();

        public bool Equals(LocationInput? x, LocationInput? y)
        {
            if (x is null || y is null)
            {
                return x is null && y is null;
            }

            return x.Category.Equals(y.Category, StringComparison.OrdinalIgnoreCase)
                   && x.Latitude.Equals(y.Latitude)
                   && x.Longitude.Equals(y.Longitude)
                   && string.Equals(x.Label, y.Label, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(LocationInput obj)
        {
            return HashCode.Combine(
                obj.Category.ToUpperInvariant(),
                obj.Latitude,
                obj.Longitude,
                obj.Label?.ToUpperInvariant());
        }
    }
}



