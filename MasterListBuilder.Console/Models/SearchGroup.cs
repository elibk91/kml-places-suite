using PlacesGatherer.Console.Models;

namespace MasterListBuilder.Console.Models;

public sealed class SearchGroup
{
    public string Name { get; init; } = string.Empty;

    public string Mode { get; init; } = "tiled";

    public bool ApplyCategoryFilter { get; init; } = true;

    public IReadOnlyList<PlacesSearchDefinition> Searches { get; init; } = Array.Empty<PlacesSearchDefinition>();
}
