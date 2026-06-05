using System.Text.Json.Serialization;

namespace HubSpotLeadSync;

public enum LeadSource { Bayut, Dubizzle, OrganicWeb, OrganicApp, Referral, Other }

public enum OpportunityState { Open, Closed }

/// <summary>
/// Everything we know about a lead at a given step, from any source.
/// All fields except the eventual <see cref="OpportunityId"/> are optional so we can
/// sync progressively. The /leads endpoint mints an OpportunityId if none is supplied
/// (organic / anonymous), so by the time the worker runs it is always set.
/// </summary>
public sealed class LeadSyncRequest
{
    /// <summary>Our own id for the opportunity (the dedup anchor on the Deal). Minted if absent.</summary>
    public string? OpportunityId { get; set; }

    public LeadSource Source { get; set; } = LeadSource.Other;

    /// <summary>Bayut/Dubizzle reference — stored for traceability only, never the dedup key.</summary>
    public string? PartnerLeadRef { get; set; }

    // --- Person ---
    public bool IsAuthenticated { get; set; }
    public string? CustomerId { get; set; }          // our stable customer id, if any
    public string? AnonymousSessionId { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }

    // --- Known HubSpot ids (skip lookup if present) ---
    public string? HubSpotContactId { get; set; }
    public string? HubSpotDealId { get; set; }

    // --- Deal / top-of-funnel ---
    public string? DealName { get; set; }
    public string? PipelineStage { get; set; }
    public decimal? Amount { get; set; }

    // --- Retargeting signals (offer is a human-readable snapshot, not a product object) ---
    public string? DroppedAt { get; set; }           // e.g. "offer_selection"
    public string? OffersSeenSnapshot { get; set; }  // e.g. "ADCB 4.19%, ENBD 4.35%"

    public Dictionary<string, string> ExtraContactProps { get; set; } = new();
    public Dictionary<string, string> ExtraDealProps { get; set; } = new();
}

public sealed class LeadSyncResult
{
    public string? ContactId { get; set; }
    public string? DealId { get; set; }
    public bool ContactCreated { get; set; }
    public bool DealCreated { get; set; }
    public bool Held { get; set; }   // anonymous + unidentifiable: nothing pushed to HubSpot yet
    public string Notes { get; set; } = "";
}

/// <summary>A pending change to push to HubSpot (transactional outbox).</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = "LeadUpsert";
    public string PayloadJson { get; set; } = "";
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A single HubSpot webhook event (events arrive as a JSON array of these).</summary>
public sealed class WebhookEvent
{
    [JsonPropertyName("eventId")] public long EventId { get; set; }
    [JsonPropertyName("subscriptionType")] public string SubscriptionType { get; set; } = "";
    [JsonPropertyName("objectId")] public long ObjectId { get; set; }
    [JsonPropertyName("propertyName")] public string? PropertyName { get; set; }
    [JsonPropertyName("propertyValue")] public string? PropertyValue { get; set; }
    [JsonPropertyName("occurredAt")] public long OccurredAt { get; set; }
    [JsonPropertyName("changeSource")] public string? ChangeSource { get; set; }
    [JsonPropertyName("sourceId")] public string? SourceId { get; set; }
}

/// <summary>Our local record linking an opportunity to its HubSpot ids (the "DB" seam).</summary>
public sealed class OpportunityRecord
{
    public required string OpportunityId { get; set; }
    public string? CustomerId { get; set; }
    public string? HubSpotContactId { get; set; }
    public string? HubSpotDealId { get; set; }
    public OpportunityState State { get; set; } = OpportunityState.Open;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
