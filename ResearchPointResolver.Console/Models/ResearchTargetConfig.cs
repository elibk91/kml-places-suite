using PlacesGatherer.Console.Models;

namespace ResearchPointResolver.Console.Models;

public sealed class ResearchTargetConfig
{
    public RectangleBounds Bounds { get; init; } = new();

    public SecretSettings Secrets { get; init; } = new();

    public IReadOnlyList<ResearchTarget> Targets { get; init; } = Array.Empty<ResearchTarget>();
}
