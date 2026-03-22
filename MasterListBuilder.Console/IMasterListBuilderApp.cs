namespace MasterListBuilder.Console;
public interface IMasterListBuilderApp
{
    Task<int> RunAsync(string[] args, TextWriter output, TextWriter error);
}
