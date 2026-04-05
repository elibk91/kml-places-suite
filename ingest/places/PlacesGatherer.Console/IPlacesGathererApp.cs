namespace PlacesGatherer.Console;
public interface IPlacesGathererApp
{
    Task<int> RunAsync(string[] args, TextWriter output, TextWriter error);
}
