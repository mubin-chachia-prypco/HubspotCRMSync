using System.Globalization;

namespace HubSpotLeadSync;

/// <summary>
/// Owns identity resolution and de-duplication for all sources (partner + organic).
///
/// Two independent questions per lead:
///   1. Who is the person?   -> resolve/create Contact (customer id -> email -> phone).
///   2. New opportunity or continuation? -> reuse the open deal, else create a new one.
///
/// Anonymous + unidentifiable leads are held locally (no junk HubSpot contact created).
/// </summary>
public sealed class LeadSyncService(HubSpotClient hs, IOpportunityStore store, IEchoGuard echo, DealStageMap stages, ILogger<LeadSyncService> log)
{
    public const string OpportunityIdProp = "opportunity_id";    // unique on Deal — the dedup anchor
    public const string CustomerIdProp = "portal_customer_id";   // unique on Contact (if available)

    public async Task<LeadSyncResult> SyncAsync(LeadSyncRequest req, CancellationToken ct = default)
    {
        var result = new LeadSyncResult();

        // 1) Resolve the person.
        var contactId = await ResolveContactAsync(req, result, ct);
        if (contactId is null)
        {
            result.Held = true;
            result.Notes = "Anonymous lead held locally until an email/phone or login arrives.";
            PersistLocal(req, contactId: null, dealId: null);
            return result;
        }

        // 2) Resolve the opportunity (reuse open deal vs create new).
        var dealId = await ResolveDealAsync(req, contactId, result, ct);

        // 3) Link them (idempotent).
        await hs.AssociateDefaultAsync("contacts", contactId, "deals", dealId, ct);

        // Remember we wrote these, so the resulting webhooks aren't treated as external changes.
        echo.MarkWritten("contacts", contactId);
        echo.MarkWritten("deals", dealId);
        PersistLocal(req, contactId, dealId);

        result.ContactId = contactId;
        result.DealId = dealId;
        return result;
    }

    private async Task<string?> ResolveContactAsync(LeadSyncRequest req, LeadSyncResult result, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(req.HubSpotContactId))
        {
            await hs.UpdateAsync("contacts", req.HubSpotContactId!, ContactProps(req), ct);
            return req.HubSpotContactId;
        }

        string? existing = null;
        if (!string.IsNullOrWhiteSpace(req.CustomerId))
            existing = await hs.FindIdByPropertyAsync("contacts", CustomerIdProp, req.CustomerId!, ct);
        if (existing is null && !string.IsNullOrWhiteSpace(req.Email))
            existing = await hs.FindIdByPropertyAsync("contacts", "email", req.Email!, ct);
        if (existing is null && !string.IsNullOrWhiteSpace(req.Phone))
            existing = await hs.FindIdByPropertyAsync("contacts", "phone", req.Phone!, ct);

        if (existing is not null)
        {
            await hs.UpdateAsync("contacts", existing, ContactProps(req), ct);
            return existing;
        }

        // Anonymous & unidentifiable: don't create a junk contact yet.
        if (string.IsNullOrWhiteSpace(req.Email) && string.IsNullOrWhiteSpace(req.Phone)
            && string.IsNullOrWhiteSpace(req.CustomerId))
            return null;

        try
        {
            var id = await hs.CreateAsync("contacts", ContactProps(req), ct);
            result.ContactCreated = true;
            return id;
        }
        catch (ContactAlreadyExistsException ex)
        {
            await hs.UpdateAsync("contacts", ex.ExistingId, ContactProps(req), ct);
            return ex.ExistingId;
        }
    }

    private async Task<string> ResolveDealAsync(LeadSyncRequest req, string contactId, LeadSyncResult result, CancellationToken ct)
    {
        // Explicit HubSpot deal id supplied -> direct update.
        if (!string.IsNullOrWhiteSpace(req.HubSpotDealId))
        {
            await hs.UpdateAsync("deals", req.HubSpotDealId!, DealProps(req), ct);
            return req.HubSpotDealId!;
        }

        var oppId = req.OpportunityId!; // minted by the endpoint if absent

        // Caller passed back a known opportunityId -> continuation of an existing deal.
        var rec = store.GetByOpportunityId(oppId);
        if (rec?.HubSpotDealId is not null)
        {
            await hs.UpdateAsync("deals", rec.HubSpotDealId, DealProps(req), ct);
            return rec.HubSpotDealId;
        }

        // Local store has no record (e.g. after restart) -> search HubSpot by opportunityId.
        var byOpp = await hs.FindIdByPropertyAsync("deals", OpportunityIdProp, oppId, ct);
        if (byOpp is not null)
        {
            await hs.UpdateAsync("deals", byOpp, DealProps(req), ct);
            return byOpp;
        }

        // Partner source with a partnerLeadRef -> dedup by the partner's own reference.
        // Same ref = same deal (update). New ref = new deal (create below).
        var isPartner = req.Source is not (LeadSource.OrganicWeb or LeadSource.OrganicApp);
        if (isPartner && !string.IsNullOrWhiteSpace(req.PartnerLeadRef))
        {
            var byRef = await hs.FindIdByPropertyAsync("deals", "partner_lead_ref", req.PartnerLeadRef!, ct);
            if (byRef is not null)
            {
                await hs.UpdateAsync("deals", byRef, DealProps(req), ct);
                return byRef;
            }
        }

        // Organic source, or partner with a new ref -> new deal.
        var id = await hs.CreateAsync("deals", DealProps(req, isCreate: true), ct);
        result.DealCreated = true;
        return id;
    }

    private static Dictionary<string, string> ContactProps(LeadSyncRequest req)
    {
        var p = new Dictionary<string, string>();
        Add(p, CustomerIdProp, req.CustomerId);
        Add(p, "email", req.Email);
        Add(p, "phone", req.Phone);
        Add(p, "firstname", req.FirstName);
        Add(p, "lastname", req.LastName);
        Add(p, "lead_source", req.Source.ToString());
        foreach (var (k, v) in req.ExtraContactProps) p[k] = v;
        return p;
    }

    private Dictionary<string, string> DealProps(LeadSyncRequest req, bool isCreate = false)
    {
        var p = new Dictionary<string, string> { [OpportunityIdProp] = req.OpportunityId! };
        Add(p, "dealname", req.DealName ?? (isCreate ? $"Mortgage {req.OpportunityId}" : null));
        Add(p, "partner_lead_ref", req.PartnerLeadRef);
        Add(p, "lead_source", req.Source.ToString());
        Add(p, "customer_profile_snapshot", req.CustomerProfileSnapshot);
        Add(p, "dropped_at", req.DroppedAt);
        Add(p, "offers_seen_snapshot", req.OffersSeenSnapshot);
        if (req.Amount is { } amt) p["amount"] = amt.ToString(CultureInfo.InvariantCulture);
        foreach (var (k, v) in req.ExtraDealProps) p[k] = v;
        return p;
    }

    private void PersistLocal(LeadSyncRequest req, string? contactId, string? dealId)
    {
        var rec = store.GetByOpportunityId(req.OpportunityId!) ?? new OpportunityRecord { OpportunityId = req.OpportunityId! };
        rec.CustomerId = req.CustomerId ?? rec.CustomerId;
        rec.HubSpotContactId = contactId ?? rec.HubSpotContactId;
        rec.HubSpotDealId = dealId ?? rec.HubSpotDealId;
        rec.State = OpportunityState.Open;
        store.Save(rec);
    }

    private static void Add(IDictionary<string, string> d, string k, string? v)
    {
        if (!string.IsNullOrWhiteSpace(v)) d[k] = v!;
    }
}
