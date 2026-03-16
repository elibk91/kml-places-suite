using PlacesGatherer.Console.Models;

namespace PlacesGatherer.Console.Services;

/// <summary>
/// Expands a configured search into one base query plus optional related queries for access-heavy places.
/// </summary>
public static class PlacesSearchExpander
{
    public static IReadOnlyList<PlacesSearchDefinition> Expand(PlacesSearchDefinition search)
    {
        var expanded = new List<PlacesSearchDefinition>
        {
            search with { SourceQueryType = "base", Expansion = null }
        };

        if (search.Expansion is null || !search.Expansion.Enabled)
        {
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
                Expansion = null
            });
        }

        return expanded;
    }
}
