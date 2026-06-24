using Domain.Common;

namespace Domain.Entities.InboundLeads
{
    /// <summary>
    /// A lead posted directly to us by a partner (Dubizzle/Bayut) at top-of-funnel. We store the raw
    /// payload opaquely and hand back <see cref="Id"/> as a one-time, short-lived token; the FE
    /// redeems it on the PRYPCO landing page to prefill the affordability calculator. Store-only —
    /// no HubSpot write happens here. See docs/forwarder-adapter-spec.md §15.
    /// </summary>
    public class InboundLead
    {
        /// <summary>
        /// UUIDv7 — sortable (time-ordered) AND the URL token (non-enumerable). Minted by us on
        /// insert; the partner never sends an id.
        /// </summary>
        public Guid Id { get; set; } = UuidV7.New();

        /// <summary>'dubizzle' | 'bayut' | ... — which partner posted the lead.</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>Raw partner payload, stored opaquely as jsonb. We never parse it; the FE reads it.</summary>
        public string Payload { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>One-time token TTL (~5 min). Redeem fails once past this.</summary>
        public DateTimeOffset ExpiresAt { get; set; }

        /// <summary>Set on first successful redeem. Non-null ⇒ already consumed (one-time).</summary>
        public DateTimeOffset? ConsumedAt { get; set; }
    }
}
