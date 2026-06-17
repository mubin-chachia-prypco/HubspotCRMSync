using Application.IServices;
using Application.IServices.IConsumers;
using Application.Messages;
using Application.Options;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Prypto.ServiceBusHelpers;
using Prypto.ServiceBusHelpers.Consumer;
using Prypto.ServiceBusHelpers.Options;

namespace Application.Consumers
{
    /// <summary>
    /// Reads HubSpot-sync envelopes off Service Bus and forwards them to the adapter Function.
    /// Same pattern as InstaMortgageService's MoEngageConsumer/SalesforceConsumer: inherit
    /// BaseConsumer, set QueueName, override MessageProcessing, complete on success.
    /// </summary>
    public class HubSpotConsumer : BaseConsumer, IHubSpotConsumer
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public HubSpotConsumer(
            ILogger<HubSpotConsumer> logger,
            IOptionsMonitor<ServiceBusSettings> serviceBusSettings,
            IQueueCheckService queueCheckService,
            IOptionsMonitor<HubSpotSyncSettings> hubSpotSyncSettings,
            IServiceScopeFactory scopeFactory)
            : base(logger, queueCheckService, serviceBusSettings)
        {
            QueueName = hubSpotSyncSettings.CurrentValue.QueueName;
            QueueCheckService.CheckQueueExistsAsync(QueueName);
            _scopeFactory = scopeFactory;
        }

        protected override async Task MessageProcessing(ProcessMessageEventArgs args)
        {
            using var scope = _scopeFactory.CreateScope();
            var adapter = scope.ServiceProvider.GetRequiredService<IHubSpotAdapterClient>();

            try
            {
                var message = JsonConvert.DeserializeObject<HubSpotSyncMessage>(args.Message.Body.ToString());
                if (message == null)
                {
                    Logger.LogError("Could not deserialize HubSpot sync message. Body: {Body}", args.Message.Body.ToString());
                    return; // leave for retry/DLQ per SB policy
                }

                var ok = await adapter.SendAsync(message, args.CancellationToken);
                if (ok)
                {
                    await args.CompleteMessageAsync(args.Message);
                    Logger.LogInformation("HubSpot sync '{Key}' ({ObjectType}) consumed.", message.IdempotencyKey, message.ObjectType);
                }
                else
                {
                    Logger.LogError("Adapter rejected HubSpot sync '{Key}'; will retry.", message.IdempotencyKey);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error processing HubSpot sync message: {Error}", ex.Message);
            }
        }
    }
}
