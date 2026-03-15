using KmlGenerator.Core.Models;

namespace KmlGenerator.Core.Services;

public interface IKmlGenerationService
{
    GenerateKmlResult Generate(GenerateKmlRequest request);
}
