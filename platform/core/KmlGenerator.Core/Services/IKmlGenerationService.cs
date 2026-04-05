using KmlGenerator.Core.Models;

namespace KmlGenerator.Core.Services;

public interface IKmlGenerationService
{
    GenerateKmlResult Generate(GenerateKmlRequest request);

    CoverageDiagnosticResult DiagnoseCoverage(GenerateKmlRequest request, double latitude, double longitude, double radiusMiles, int topPerCategory);
}
