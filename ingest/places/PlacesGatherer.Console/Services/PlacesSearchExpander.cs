using Microsoft.Extensions.Logging;
using PlacesGatherer.Console.Models;

namespace PlacesGatherer.Console.Services;

/// <summary>
/// Expands a configured search into one base query plus optional related queries for access-heavy places.
/// </summary>
public sealed class PlacesSearchExpander : IPlacesSearchExpander
{
    private readonly ILogger<PlacesSearchExpander> _logger;

    public PlacesSearchExpander(ILogger<PlacesSearchExpander> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<PlacesSearchDefinition> Expand(PlacesSearchDefinition search)
    {
        var expanded = new List<PlacesSearchDefinition>
        {
            search with { SourceQueryType = "base", Expansion = new PlacesSearchExpansion() }
        };

        if (!search.Expansion.Enabled)
        {
            _logger.LogDebug("No expansion templates enabled for query {Query}", search.Query);
            return expanded;
        }

        foreach (var template in search.Expansion.Templates.Where(static template => !string.IsNullOrWhiteSpace(template)))
        {
            var query = template.Contains("{query}", StringComparison.OrdinalIgnoreCase)
                ? template.Replace("{query}", search.Query, StringComparison.OrdinalIgnoreCase)
                : $"{search.Query} {template.Trim()}";

            expanded.Add(search with
            {
                Query = query.Trim(),
                SourceQueryType = "expanded",
                Expansion = new PlacesSearchExpansion()
            });
        }

        _logger.LogInformation(
            "Expanded query {Query} into {ExpandedCount} search variants",
            search.Query,
            expanded.Count);

        return expanded;
    }
}
