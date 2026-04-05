using PlacesGatherer.Console.Models;
namespace PlacesGatherer.Console.Services;
public interface IGooglePlacesClient
{
    Task<IReadOnlyList<NormalizedPlaceRecord>> SearchAsync(
        PlacesSearchDefinition search,
        RectangleBounds bounds,
        string apiKey,
        CancellationToken cancellationToken = default);
}
