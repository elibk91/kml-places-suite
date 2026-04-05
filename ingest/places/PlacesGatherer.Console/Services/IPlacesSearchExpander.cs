using PlacesGatherer.Console.Models;

namespace PlacesGatherer.Console.Services;

public interface IPlacesSearchExpander
{
    IReadOnlyList<PlacesSearchDefinition> Expand(PlacesSearchDefinition search);
}
