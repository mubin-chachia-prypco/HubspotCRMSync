using Domain.Entities.InboundLeads;

namespace Application.IRepositories
{
    /// <summary>
    /// Store-and-redeem for partner (Dubizzle/Bayut) intake leads (spec §15). Unlike the outbox,
    /// these commit on their own — intake is its own transaction, not part of a domain write.
    /// </summary>
    public interface IInboundLeadRepository
    {
        /// <summary>Persist a raw partner payload and return the row (its <c>Id</c> is the token).</summary>
        Task<InboundLead> CreateAsync(string source, string payloadJson, TimeSpan ttl, CancellationToken cancellationToken);

        /// <summary>
        /// Redeem a token: returns the lead and marks it consumed (one-time). Returns null if the
        /// token is unknown, already consumed, or expired.
        /// </summary>
        Task<InboundLead?> RedeemAsync(Guid token, CancellationToken cancellationToken);
    }
}
