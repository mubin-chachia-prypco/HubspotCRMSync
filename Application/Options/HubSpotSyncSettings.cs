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

        /// <summary>Public URL of the HubSpot adapter Azure Function (the JS box), e.g. https://hubspot-adapter.azurewebsites.net/api/ingest.</summary>
        public string AdapterIngestUrl { get; set; } = string.Empty;

        /// <summary>
        /// Entra scope used to get a Managed Identity token for the Function, e.g.
        /// "api://&lt;function-app-id&gt;/.default". Empty disables bearer auth (e.g. local dev).
        /// </summary>
        public string AdapterScope { get; set; } = string.Empty;
    }
}
