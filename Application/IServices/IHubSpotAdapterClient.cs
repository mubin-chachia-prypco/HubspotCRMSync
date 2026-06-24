using Application.Messages;

namespace Application.IServices
{
    /// <summary>
    /// Calls the HubSpot adapter API (TypeScript + Fastify on k8s). The ONLY outbound hop that touches
    /// anything HubSpot-related, and even it only knows a URL + the generic envelope / enum vocab.
    /// </summary>
    public interface IHubSpotAdapterClient
    {
        /// <summary>Returns true on a 2xx from the adapter (so the consumer can complete the SB message).</summary>
        Task<bool> SendAsync(HubSpotSyncMessage message, CancellationToken cancellationToken);

        /// <summary>
        /// Fetches the adapter's generic enum vocabulary (raw JSON) so this service can expose it to the
        /// FE. Keeps the CRM-specific enum values inside the adapter. Throws on a non-2xx.
        /// </summary>
        Task<string> GetEnumsAsync(CancellationToken cancellationToken);
    }
}
