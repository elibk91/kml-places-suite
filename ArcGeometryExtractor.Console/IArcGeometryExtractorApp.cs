public interface IArcGeometryExtractorApp
{
    Task<int> RunAsync(string[] args, TextWriter output, TextWriter error);
}
