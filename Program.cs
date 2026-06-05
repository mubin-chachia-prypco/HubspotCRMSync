using System.Text.Json;
using HubSpotLeadSync;

var builder = WebApplication.CreateBuilder(args);

// --- Options (env vars win over appsettings; never commit real secrets) ---
var hubspot = builder.Configuration.GetSection("HubSpot").Get<HubSpotOptions>() ?? new HubSpotOptions();
hubspot.AccessToken = Environment.GetEnvironmentVariable("HUBSPOT_TOKEN") ?? hubspot.AccessToken;
hubspot.ClientSecret = Environment.GetEnvironmentVariable("HUBSPOT_CLIENT_SECRET") ?? hubspot.ClientSecret;
hubspot.BaseUrl = Environment.GetEnvironmentVariable("HUBSPOT_BASE_URL") ?? hubspot.BaseUrl;
builder.Services.AddSingleton(hubspot);

// Accept/emit enum names (e.g. "OrganicWeb") in request/response JSON.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// --- Services ---
builder.Services.AddHttpClient<HubSpotClient>();
builder.Services.AddSingleton<IOpportunityStore, InMemoryOpportunityStore>();
builder.Services.AddSingleton<IOutbox, InMemoryOutbox>();
builder.Services.AddSingleton<IProcessedEvents, InMemoryProcessedEvents>();
builder.Services.AddSingleton<IEchoGuard, InMemoryEchoGuard>();
builder.Services.AddSingleton<WebhookSignatureValidator>();
builder.Services.AddSingleton<DealStageMap>();
builder.Services.AddSingleton<LocalMirror>();
builder.Services.AddSingleton<InboundEventProcessor>();
builder.Services.AddScoped<LeadSyncService>();
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddHostedService<ReconciliationWorker>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// --- Outbound: a lead from the portal. Persist + enqueue, return fast (transactional outbox). ---
app.MapPost("/leads", (LeadSyncRequest req, IOutbox outbox) =>
{
    // Mint our own opportunity id if none supplied (organic / anonymous).
    req.OpportunityId ??= $"OPP-{Guid.NewGuid():N}";
    // In a real impl the lead row + this outbox row are written in ONE DB transaction.
    outbox.Enqueue(new OutboxMessage { PayloadJson = JsonSerializer.Serialize(req) });
    return Results.Accepted($"/opportunities/{req.OpportunityId}", new { req.OpportunityId, queued = true });
});

// --- Inbound: HubSpot webhooks. Validate signature, ack fast, process out of band. ---
app.MapPost("/webhooks/hubspot", async (HttpRequest http, WebhookSignatureValidator validator, InboundEventProcessor processor) =>
{
    using var reader = new StreamReader(http.Body);
    var body = await reader.ReadToEndAsync();

    var fullUri = $"{http.Scheme}://{http.Host}{http.Path}{http.QueryString}";
    var valid = validator.IsValid(
        http.Method, fullUri, body,
        http.Headers["X-HubSpot-Request-Timestamp"].FirstOrDefault(),
        http.Headers["X-HubSpot-Signature-v3"].FirstOrDefault());

    if (!valid) return Results.Unauthorized();

    var events = JsonSerializer.Deserialize<List<WebhookEvent>>(body) ?? [];
    _ = Task.Run(() => processor.ProcessAsync(events)); // ack fast; process async (PoC)
    return Results.Ok();
});

app.Run();
