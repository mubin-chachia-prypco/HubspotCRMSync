namespace HubSpotLeadSync;

/// <summary>
/// The set of HubSpot properties HubSpot is the owner of (top-of-funnel / qualification).
/// Only these flow HubSpot -> our local mirror; everything else (customer-submitted data,
/// or anything owned by Pulse) is ignored on the inbound path. See plan §7.
/// </summary>
public static class HubSpotOwnedFields
{
    public static readonly IReadOnlySet<string> Contact =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "lifecyclestage", "hs_lead_status", "hubspot_owner_id" };

    public static readonly IReadOnlySet<string> Deal =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dealstage", "hubspot_owner_id" };

    public static IReadOnlySet<string> For(string objectType) => objectType == "deals" ? Deal : Contact;

    public static bool IsOwned(string objectType, string? property) =>
        property is not null && For(objectType).Contains(property);
}

/// <summary>
/// Applies a HubSpot-owned property change to our local opportunity mirror. Shared by the
/// inbound webhook processor and the reconciliation sweep so both honour the same ownership
/// and last-writer rules.
/// </summary>
public sealed class LocalMirror(IOpportunityStore store, DealStageMap stages, ILogger<LocalMirror> log)
{
    /// <summary>
    /// Apply one HubSpot-owned property change. Returns true if the mirror was updated.
    /// No-ops (and says why) when the property isn't HubSpot-owned, no local record links the
    /// object, or the change is older than the last one we mirrored.
    /// </summary>
    public bool ApplyChange(string objectType, string objectId, string? propertyName, string? propertyValue, DateTimeOffset occurredAt)
    {
        if (!HubSpotOwnedFields.IsOwned(objectType, propertyName))
        {
            log.LogDebug("Ignoring non-owned property {Prop} on {Type} {Id}", propertyName, objectType, objectId);
            return false;
        }

        var rec = objectType == "deals" ? store.GetByHubSpotDealId(objectId) : store.GetByHubSpotContactId(objectId);
        if (rec is null)
        {
            log.LogInformation("No local opportunity linked to {Type} {Id}; nothing to mirror", objectType, objectId);
            return false;
        }

        if (rec.LastHubSpotChangeAt is { } last && occurredAt <= last)
        {
            log.LogDebug("Stale change for {Type} {Id} ({Occurred:o} <= {Last:o}); keeping local", objectType, objectId, occurredAt, last);
            return false;
        }

        switch (propertyName!.ToLowerInvariant())
        {
            case "lifecyclestage": rec.LifecycleStage = propertyValue; break;
            case "hs_lead_status": rec.LeadStatus = propertyValue; break;
            case "hubspot_owner_id": rec.OwnerId = propertyValue; break;
            case "dealstage":
                rec.DealStage = propertyValue;
                rec.State = stages.IsClosedStage(propertyValue) ? OpportunityState.Closed : OpportunityState.Open;
                break;
        }

        rec.LastHubSpotChangeAt = occurredAt;
        store.Save(rec);
        log.LogInformation("Mirrored {Type} {Id}: {Prop}={Val} (state={State})", objectType, objectId, propertyName, propertyValue, rec.State);
        return true;
    }
}
