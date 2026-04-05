public interface IKmlTilerApp
{
    Task<int> RunAsync(string[] args, TextWriter output, TextWriter error);
}
