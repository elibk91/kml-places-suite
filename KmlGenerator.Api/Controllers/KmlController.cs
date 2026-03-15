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

    public KmlController(IKmlGenerationService kmlGenerationService)
    {
        _kmlGenerationService = kmlGenerationService;
    }

    [HttpPost]
    [ProducesResponseType(typeof(GenerateKmlResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public ActionResult<GenerateKmlResult> Generate([FromBody] GenerateKmlRequest request)
    {
        try
        {
            return Ok(_kmlGenerationService.Generate(request));
        }
        catch (KmlValidationException exception)
        {
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
            return File(
                fileContents: System.Text.Encoding.UTF8.GetBytes(result.Kml),
                contentType: "application/vnd.google-earth.kml+xml",
                fileDownloadName: "outline.kml");
        }
        catch (KmlValidationException exception)
        {
            return ValidationProblem(detail: exception.Message);
        }
    }
}
