using Domain.Entities.InboundLeads;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations
{
    public class InboundLeadConfiguration : IEntityTypeConfiguration<InboundLead>
    {
        public void Configure(EntityTypeBuilder<InboundLead> builder)
        {
            builder.ToTable("inbound_leads");
            builder.HasKey(x => x.Id);

            // We mint the UUIDv7 ourselves — the DB must not overwrite it.
            builder.Property(x => x.Id).ValueGeneratedNever();
            builder.Property(x => x.Source).IsRequired().HasMaxLength(50);
            builder.Property(x => x.Payload).IsRequired().HasColumnType("jsonb");
            builder.Property(x => x.CreatedAt).IsRequired();
            builder.Property(x => x.ExpiresAt).IsRequired();

            // Sortable by arrival; the PK is already time-ordered (UUIDv7) but index CreatedAt for
            // operational queries / cleanup of expired rows.
            builder.HasIndex(x => x.CreatedAt);
        }
    }
}
