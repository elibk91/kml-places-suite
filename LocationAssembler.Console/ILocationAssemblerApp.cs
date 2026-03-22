namespace LocationAssembler.Console;
public interface ILocationAssemblerApp
{
    Task<int> RunAsync(string[] args, TextWriter output, TextWriter error);
}
