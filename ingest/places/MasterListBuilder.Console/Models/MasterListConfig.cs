using PlacesGatherer.Console.Models;

namespace MasterListBuilder.Console.Models;

public sealed class MasterListConfig
{
    public RectangleBounds Bounds { get; init; } = new();

    public SecretSettings Secrets { get; init; } = new();

    public double TileLatitudeStep { get; init; } = 0.05d;

    public double TileLongitudeStep { get; init; } = 0.05d;

    public IReadOnlyList<SearchGroup> Groups { get; init; } = Array.Empty<SearchGroup>();
}
