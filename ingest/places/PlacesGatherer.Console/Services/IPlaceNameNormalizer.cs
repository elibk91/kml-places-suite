using PlacesGatherer.Console.Models;

namespace PlacesGatherer.Console.Services;

public interface IPlaceNameNormalizer
{
    IReadOnlyList<NormalizedPlaceRecord> Normalize(IReadOnlyList<NormalizedPlaceRecord> records);
}
