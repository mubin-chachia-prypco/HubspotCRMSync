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
builder.Services.AddScoped<LeadSyncService>();
builder.Services.AddHostedService<OutboxWorker>();

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

// --- Query: deal + all associated applications ---
app.MapGet("/deals/{opportunityId}", async (string opportunityId, IOpportunityStore store, HubSpotClient hs, HubSpotOptions opts, CancellationToken ct) =>
{
    // Resolve HubSpot deal id from local store, fall back to HubSpot search.
    var rec = store.GetByOpportunityId(opportunityId);
    var dealHsId = rec?.HubSpotDealId
        ?? await hs.FindIdByPropertyAsync("deals", LeadSyncService.OpportunityIdProp, opportunityId, ct);

    if (dealHsId is null) return Results.NotFound(new { error = $"No deal found for opportunityId {opportunityId}" });

    // Fetch deal and applications in parallel.
    var dealTask = hs.GetObjectAsync("deals", dealHsId, ct);
    var appIdsTask = hs.GetAssociatedIdsAsync("deals", dealHsId, opts.ApplicationObjectTypeId, ct);
    await Task.WhenAll(dealTask, appIdsTask);

    var deal = await dealTask;
    var appIds = await appIdsTask;

    // Fetch all application records with all properties.
    var applications = await Task.WhenAll(appIds.Select(id => hs.GetObjectAsync(opts.ApplicationObjectTypeId, id, ct)));

    return Results.Ok(new
    {
        opportunityId,
        deal = new { id = deal!.Value.Id, properties = deal.Value.Props },
        applications = applications
            .Where(a => a is not null)
            .Select(a => new { id = a!.Value.Id, properties = a.Value.Props })
    });
});

app.Run();
