using Application.IServices;
using Application.Messages;
using Application.Options;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Prypto.ServiceBusHelpers;
using Prypto.ServiceBusHelpers.Options;

namespace Application.Services.Queue
{
    /// <summary>
    /// Produces and enqueues HubSpot-sync messages to Azure Service Bus. Same shape as
    /// InstaMortgageService's QueueMessageProducer (ServiceBusClient + IQueueCheckService),
    /// trimmed to this integration's single queue.
    /// </summary>
    public class QueueMessageProducer : IQueueMessageProducer
    {
        private readonly ILogger<QueueMessageProducer> _logger;
        private readonly IQueueCheckService _queueCheckService;
        private readonly HubSpotSyncSettings _hubSpotSyncSettings;
        private readonly ServiceBusClient _client;

        public QueueMessageProducer(
            ILogger<QueueMessageProducer> logger,
            IQueueCheckService queueCheckService,
            IOptionsMonitor<ServiceBusSettings> serviceBusSettings,
            IOptionsMonitor<HubSpotSyncSettings> hubSpotSyncSettings)
        {
            _logger = logger;
            _queueCheckService = queueCheckService;
            _hubSpotSyncSettings = hubSpotSyncSettings.CurrentValue;
            var connectionString = serviceBusSettings?.CurrentValue?.ConnectionString
                ?? throw new ArgumentNullException(nameof(serviceBusSettings));
            _client = new ServiceBusClient(connectionString);
        }

        public async Task EnqueueHubSpotSyncAsync(HubSpotSyncMessage message, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);

            var queueName = _hubSpotSyncSettings.QueueName;
            await _queueCheckService.CheckQueueExistsAsync(queueName);
            var sender = _client.CreateSender(queueName);

            var serviceBusMessage = new ServiceBusMessage(JsonConvert.SerializeObject(message))
            {
                CorrelationId = message.IdempotencyKey,
                MessageId = message.IdempotencyKey, // SB native dedup window, belt-and-suspenders
            };

            await sender.SendMessageAsync(serviceBusMessage, cancellationToken);
            _logger.LogInformation(
                "Enqueued HubSpot sync: {ObjectType}/{Operation} extId={ExternalId} key={Key}",
                message.ObjectType, message.Operation, message.ExternalId, message.IdempotencyKey);
        }
    }
}
