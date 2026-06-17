namespace Domain.Entities.Outbox
{
    /// <summary>
    /// Transactional outbox row. Written in the same EF transaction as the originating
    /// domain change, then drained to Service Bus by a relay. Mirrors the InstaMortgageService
    /// Outbox entity so this lifts across unchanged.
    /// </summary>
    public class OutboxMessage
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Logical message type (e.g. the envelope objectType + operation).</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>Serialized payload (the generic HubSpot sync envelope).</summary>
        public string Payload { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Null until the relay has published it to Service Bus.</summary>
        public DateTimeOffset? ProcessedAt { get; set; }

        public int Attempts { get; set; }
    }
}
