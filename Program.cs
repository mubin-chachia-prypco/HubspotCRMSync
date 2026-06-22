using System.Text.Json.Serialization;
using Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// --- Options + infrastructure (mirrors InstaMortgageService's registration extensions) ---
builder.Services.AddHubSpotSyncOptions(builder.Configuration);
builder.Services.ConfigureDatabase(builder.Configuration);
builder.Services.AddHubSpotSyncServices();
builder.Services.AddCQRSHandlers();

// Controllers (MVC) dispatch to MediatR command/query handlers. Enum names in JSON.
builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();

// Start the Service Bus consumer (non-local environments only), like InstaMortgageService.
app.Services.RegisterMessageConsumers();

app.Run();
