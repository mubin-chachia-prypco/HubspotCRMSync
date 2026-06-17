using Application.IRepositories;
using Application.IServices;
using Application.Messages;
using Infrastructure;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

// --- Partner lead intake (Dubizzle/Bayut), spec §15. Store-only: persist the raw payload opaquely
// and hand back a one-time, short-lived UUIDv7 token. NO HubSpot write here — the HubSpot Lead/Deal
// is created later via /ingest when the user engages. The FE redeems the token to prefill. ---
const int IntakeTtlSeconds = 60;

app.MapPost("/intake/dubizzle", async (HttpRequest req, IInboundLeadRepository leads, IConfiguration config, CancellationToken ct) =>
{
    using var reader = new StreamReader(req.Body, Encoding.UTF8);
    var raw = await reader.ReadToEndAsync(ct);
    if (string.IsNullOrWhiteSpace(raw))
        return Results.BadRequest(new { error = "empty body" });

    // Optional auth: enforced only when a secret is configured (so local dev needs none). Proposed
    // mechanism is HMAC-SHA256 over the raw body in the X-Signature header — confirm with Dubizzle.
    var secret = config["Intake:Dubizzle:HmacSecret"];
    if (!string.IsNullOrEmpty(secret) && !IntakeAuth.IsValidHmac(req, raw, secret))
        return Results.Unauthorized();

    // We store the payload as jsonb — reject anything that isn't valid JSON.
    try { using var _ = JsonDocument.Parse(raw); }
    catch (JsonException) { return Results.BadRequest(new { error = "payload must be valid JSON" }); }

    var lead = await leads.CreateAsync("dubizzle", raw, TimeSpan.FromSeconds(IntakeTtlSeconds), ct);
    return Results.Ok(new { token = lead.Id });
});

// FE redeems the token on the PRYPCO landing page. One-time + TTL: 410 if unknown/expired/consumed.
app.MapGet("/intake/dubizzle/{token:guid}", async (Guid token, IInboundLeadRepository leads, CancellationToken ct) =>
{
    var lead = await leads.RedeemAsync(token, ct);
    return lead is null
        ? Results.StatusCode(StatusCodes.Status410Gone)
        : Results.Content(lead.Payload, "application/json");
});

// Start the Service Bus consumer (non-local environments only), like InstaMortgageService.
app.Services.RegisterMessageConsumers();

app.Run();

/// <summary>Optional HMAC verification for the partner intake POST (spec §15 / §10).</summary>
static class IntakeAuth
{
    public static bool IsValidHmac(HttpRequest req, string rawBody, string secret)
    {
        if (!req.Headers.TryGetValue("X-Signature", out var provided)) return false;

        var computed = Convert.ToHexString(
            new HMACSHA256(Encoding.UTF8.GetBytes(secret)).ComputeHash(Encoding.UTF8.GetBytes(rawBody)));

        var a = Encoding.UTF8.GetBytes(computed);
        var b = Encoding.UTF8.GetBytes(provided.ToString());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
