using KmlGenerator.Core.Services;
using KmlSuite.Shared.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// API signpost: controllers stay thin and delegate everything meaningful to the shared core service.
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "HH:mm:ss.fff ";
    options.SingleLine = true;
});
builder.Services.AddKmlSuiteTracing();
builder.Services.AddTracedSingleton<IKmlGenerationService, KmlGenerationService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.MapControllers();
app.Run();

public partial class Program;
