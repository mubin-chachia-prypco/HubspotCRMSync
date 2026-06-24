namespace Application.Options
{
    /// <summary>
    /// HubSpot-sync specific config. The Service Bus connection string itself comes from the
    /// shared <c>ServiceBusSettings</c> (Prypto.ServiceBusHelpers.Options); this holds the bits
    /// that are specific to this integration.
    /// </summary>
    public class HubSpotSyncSettings
    {
        public const string SectionName = "HubSpotSyncSettings";

        /// <summary>Service Bus queue the producer writes to and the consumer reads from.</summary>
        public string QueueName { get; set; } = "hubspot-sync";

        /// <summary>URL of the HubSpot adapter API (TS+Fastify on k8s), e.g. http://hubspot-adapter/api/ingest.</summary>
        public string AdapterIngestUrl { get; set; } = string.Empty;

        /// <summary>
        /// Adapter's generic enum-vocabulary endpoint (CRM-agnostic), e.g. http://hubspot-adapter/enums.
        /// Proxied to the FE via this service's GET /enums so the FE never talks to the adapter directly.
        /// </summary>
        public string AdapterEnumsUrl { get; set; } = string.Empty;

        /// <summary>
        /// Service token presented to the adapter as the <c>X-AI-Agent-Key</c> header. Held in a
        /// secret (Key Vault) in prod. Empty disables the auth header (e.g. local dev).
        /// </summary>
        public string AdapterServiceToken { get; set; } = string.Empty;
    }
}
