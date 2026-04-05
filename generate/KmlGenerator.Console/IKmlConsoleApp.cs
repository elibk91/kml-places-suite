public interface IKmlConsoleApp
{
    Task<int> RunAsync(string[] args, TextWriter output, TextWriter error);
}
