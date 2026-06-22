namespace Application.Messages
{
    /// <summary>
    /// The CRM-agnostic envelope handed to the HubSpot adapter. This C# side never knows a
    /// HubSpot property name or type id — the adapter (Azure Function, JS) owns all of that.
    ///
    /// On migration to InstaMortgageService this can derive from Prypto.ServiceBusHelpers'
    /// BaseMessage (for InitiatedBy/CorrelationId) like the MoEngage/Salesforce messages do.
    /// </summary>
    public class HubSpotSyncMessage
    {
        /// <summary>Dedupe key for retries. The adapter is also idempotent via find-or-create.</summary>
        public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString();

        /// <summary>lead | contact | deal | application | property | offer</summary>
        public string ObjectType { get; set; } = string.Empty;

        /// <summary>upsert | update | create</summary>
        public string Operation { get; set; } = "upsert";

        /// <summary>Portal-side stable id the adapter resolves the record by (a unique CRM property).</summary>
        public string ExternalId { get; set; } = string.Empty;

        /// <summary>Portal field names → values. The adapter renames to HubSpot props.</summary>
        public Dictionary<string, string?> Properties { get; set; } = new();

        public List<HubSpotSyncAssociation> Associations { get; set; } = new();

        public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public class HubSpotSyncAssociation
    {
        public string ObjectType { get; set; } = string.Empty;
        public string ExternalId { get; set; } = string.Empty;
        public string? Label { get; set; }
    }
}
