using KmlGenerator.Core.Services;

var builder = WebApplication.CreateBuilder(args);

// API signpost: controllers stay thin and delegate everything meaningful to the shared core service.
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<IKmlGenerationService, KmlGenerationService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.MapControllers();
app.Run();

public partial class Program;
