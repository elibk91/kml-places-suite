using KmlGenerator.Core.Exceptions;
using KmlGenerator.Core.Models;
using KmlGenerator.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace KmlGenerator.Api.Controllers;

/// <summary>
/// API signpost: both endpoints delegate to the shared core service so the HTTP host stays stateless.
/// </summary>
[ApiController]
[Route("kml")]
public sealed class KmlController : ControllerBase
{
    private readonly IKmlGenerationService _kmlGenerationService;
    private readonly ILogger<KmlController> _logger;

    public KmlController(IKmlGenerationService kmlGenerationService, ILogger<KmlController> logger)
    {
        _kmlGenerationService = kmlGenerationService;
        _logger = logger;
    }

    [HttpPost]
    [ProducesResponseType(typeof(GenerateKmlResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public ActionResult<GenerateKmlResult> Generate([FromBody] GenerateKmlRequest request)
    {
        try
        {
            var result = _kmlGenerationService.Generate(request);
            _logger.LogInformation("Generated JSON KML response with {BoundaryPointCount} emitted overlap points", result.BoundaryPointCount);
            return Ok(result);
        }
        catch (KmlValidationException exception)
        {
            _logger.LogWarning(exception, "KML JSON generation failed validation");
            return ValidationProblem(detail: exception.Message);
        }
    }

    [HttpPost("file")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public IActionResult GenerateFile([FromBody] GenerateKmlRequest request)
    {
        try
        {
            var result = _kmlGenerationService.Generate(request);
            _logger.LogInformation("Generated downloadable KML file with {BoundaryPointCount} emitted overlap points", result.BoundaryPointCount);
            return File(
                fileContents: System.Text.Encoding.UTF8.GetBytes(result.Kml),
                contentType: "application/vnd.google-earth.kml+xml",
                fileDownloadName: "outline.kml");
        }
        catch (KmlValidationException exception)
        {
            _logger.LogWarning(exception, "KML file generation failed validation");
            return ValidationProblem(detail: exception.Message);
        }
    }
}
