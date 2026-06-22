using Domain.Entities.InboundLeads;
using Domain.Entities.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence
{
    /// <summary>
    /// EF Core context (PostgreSQL via Npgsql), same shape as InstaMortgageService's AppDbContext
    /// but trimmed to only what this integration needs (the transactional outbox). New entities
    /// go in here with a matching configuration under Persistence/Configurations.
    /// </summary>
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        /// <summary>Partner (Dubizzle/Bayut) intake leads — store-and-redeem prefill cache (spec §15).</summary>
        public DbSet<InboundLead> InboundLeads => Set<InboundLead>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        }
    }
}
