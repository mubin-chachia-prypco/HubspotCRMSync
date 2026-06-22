using Application.IRepositories;
using Domain.Entities.InboundLeads;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// EF Core implementation of <see cref="IInboundLeadRepository"/>. Commits on its own — intake
    /// is a standalone transaction (store-only; no HubSpot write here). See spec §15.
    /// </summary>
    public class InboundLeadRepository : IInboundLeadRepository
    {
        private readonly AppDbContext _appDbContext;

        public InboundLeadRepository(AppDbContext appDbContext)
        {
            _appDbContext = appDbContext;
        }

        public async Task<InboundLead> CreateAsync(string source, string payloadJson, TimeSpan ttl, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

            var now = DateTimeOffset.UtcNow;
            var lead = new InboundLead
            {
                Source = source,
                Payload = payloadJson,
                CreatedAt = now,
                ExpiresAt = now.Add(ttl),
            };

            await _appDbContext.InboundLeads.AddAsync(lead, cancellationToken);
            await _appDbContext.SaveChangesAsync(cancellationToken);
            return lead;
        }

        public async Task<InboundLead?> RedeemAsync(Guid token, CancellationToken cancellationToken)
        {
            var lead = await _appDbContext.InboundLeads.FirstOrDefaultAsync(x => x.Id == token, cancellationToken);
            if (lead is null) return null;

            // One-time + TTL: reject if already consumed or expired.
            if (lead.ConsumedAt is not null) return null;
            if (lead.ExpiresAt <= DateTimeOffset.UtcNow) return null;

            lead.ConsumedAt = DateTimeOffset.UtcNow;
            await _appDbContext.SaveChangesAsync(cancellationToken);
            return lead;
        }
    }
}
