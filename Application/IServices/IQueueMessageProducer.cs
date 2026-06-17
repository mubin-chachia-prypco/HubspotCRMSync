using Application.Messages;

namespace Application.IServices
{
    /// <summary>Enqueues a HubSpot sync envelope onto Service Bus (off the request path).</summary>
    public interface IQueueMessageProducer
    {
        Task EnqueueHubSpotSyncAsync(HubSpotSyncMessage message, CancellationToken cancellationToken = default);
    }
}
