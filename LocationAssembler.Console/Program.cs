using System.Text.Json;
using KmlGenerator.Core.Models;
using PlacesGatherer.Console.Models;

return await LocationAssemblerRunner.RunAsync(args, Console.Out, Console.Error);

/// <summary>
/// Console signpost: this host converts normalized gathered points into the flat KML request contract.
/// </summary>
public static class LocationAssemblerRunner
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
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
                }
            }

            var locations = records
                .Select(record => new LocationInput
                {
                    Latitude = record.Latitude,
                    Longitude = record.Longitude,
                    Category = record.Category
                })
                .Distinct(LocationInputComparer.Instance)
                .ToArray();

            var request = new GenerateKmlRequest
            {
                Locations = locations
            };

            await File.WriteAllTextAsync(parsed.Value.OutputPath, JsonSerializer.Serialize(request, JsonOptions));
            await output.WriteLineAsync($"Saved {locations.Length} unique points to {parsed.Value.OutputPath}");
            return 0;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static (IReadOnlyList<string> InputPaths, string OutputPath)? ParseArguments(IReadOnlyList<string> args)
    {
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

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
                   && x.Longitude.Equals(y.Longitude);
        }

        public int GetHashCode(LocationInput obj)
        {
            return HashCode.Combine(
                obj.Category.ToUpperInvariant(),
                obj.Latitude,
                obj.Longitude);
        }
    }
}
