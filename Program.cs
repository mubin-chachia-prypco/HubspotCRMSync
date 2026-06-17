using Application.IServices;
using Application.Messages;
using Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// --- Options + infrastructure (mirrors InstaMortgageService's registration extensions) ---
builder.Services.AddHubSpotSyncOptions(builder.Configuration);
builder.Services.ConfigureDatabase(builder.Configuration);
builder.Services.AddHubSpotSyncServices();

// Accept/emit enum names in request/response JSON.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// --- Ingress: a CRM-agnostic envelope from the portal / instamortgage. Enqueue, return fast. ---
// The HubSpot adapter (Azure Function, JS) owns ALL HubSpot mapping. Nothing here knows a
// HubSpot property name or type id.
app.MapPost("/ingest", async (HubSpotSyncMessage message, IQueueMessageProducer producer, CancellationToken ct) =>
{
    await producer.EnqueueHubSpotSyncAsync(message, ct);
    return Results.Accepted($"/ingest/{message.IdempotencyKey}", new { message.IdempotencyKey, queued = true });
});

// Start the Service Bus consumer (non-local environments only), like InstaMortgageService.
app.Services.RegisterMessageConsumers();

app.Run();
