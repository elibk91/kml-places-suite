using System.Text.RegularExpressions;
using PlacesGatherer.Console.Models;

namespace PlacesGatherer.Console.Services;

/// <summary>
/// Resolves concise final names while keeping duplicate place names distinguishable.
/// </summary>
public static partial class PlaceNameNormalizer
{
    public static IReadOnlyList<NormalizedPlaceRecord> Normalize(IReadOnlyList<NormalizedPlaceRecord> records)
    {
        var normalized = records
            .Select(record => record with { Name = BuildBaseName(record) })
            .ToArray();

        var grouped = normalized
            .GroupBy(record => $"{record.Category}::{record.Name}", StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1);

        foreach (var group in grouped)
        {
            var withHints = group
                .Select(record => record with { Name = BuildNameWithHint(record) })
                .ToArray();

            foreach (var record in withHints)
            {
                Replace(normalized, record);
            }

            var remainingConflicts = withHints
                .GroupBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
                .Where(conflict => conflict.Count() > 1);

            foreach (var conflict in remainingConflicts)
            {
                foreach (var record in conflict)
                {
                    Replace(normalized, record with
                    {
                        Name = $"{record.Name} | {BuildStableSuffix(record.PlaceId)}"
                    });
                }
            }
        }

        return normalized;
    }

    private static void Replace(NormalizedPlaceRecord[] records, NormalizedPlaceRecord updated)
    {
        var index = Array.FindIndex(records, record =>
            record.PlaceId.Equals(updated.PlaceId, StringComparison.Ordinal) &&
            record.Query.Equals(updated.Query, StringComparison.Ordinal) &&
            record.SourceQueryType.Equals(updated.SourceQueryType, StringComparison.Ordinal));

        if (index >= 0)
        {
            records[index] = updated;
        }
    }

    private static string BuildBaseName(NormalizedPlaceRecord record)
    {
        var candidate = string.IsNullOrWhiteSpace(record.Name) ? record.Query : record.Name;
        return CollapseWhitespace(candidate);
    }

    private static string BuildNameWithHint(NormalizedPlaceRecord record)
    {
        var hint = ExtractAddressHint(record.FormattedAddress);
        return string.IsNullOrWhiteSpace(hint)
            ? BuildBaseName(record)
            : $"{BuildBaseName(record)} | {hint}";
    }

    private static string ExtractAddressHint(string? formattedAddress)
    {
        if (string.IsNullOrWhiteSpace(formattedAddress))
        {
            return string.Empty;
        }

        var firstSegment = formattedAddress.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(firstSegment))
        {
            return string.Empty;
        }

        firstSegment = StreetNumberPrefix().Replace(firstSegment, string.Empty).Trim();
        return CollapseWhitespace(firstSegment);
    }

    private static string BuildStableSuffix(string placeId)
    {
        if (string.IsNullOrWhiteSpace(placeId))
        {
            return "id";
        }

        return placeId.Length <= 4 ? placeId : placeId[^4..];
    }

    private static string CollapseWhitespace(string value) =>
        MultipleWhitespace().Replace(value.Trim(), " ");

    [GeneratedRegex(@"^\d+[A-Za-z\-]*\s+")]
    private static partial Regex StreetNumberPrefix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleWhitespace();
}
