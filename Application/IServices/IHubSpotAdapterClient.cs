using Application.Messages;

namespace Application.IServices
{
    /// <summary>
    /// Calls the HubSpot adapter Azure Function (the JS box). This is the ONLY outbound hop that
    /// touches anything HubSpot-related, and even it only knows a URL + the generic envelope.
    /// </summary>
    public interface IHubSpotAdapterClient
    {
        /// <summary>Returns true on a 2xx from the adapter (so the consumer can complete the SB message).</summary>
        Task<bool> SendAsync(HubSpotSyncMessage message, CancellationToken cancellationToken);
    }
}
