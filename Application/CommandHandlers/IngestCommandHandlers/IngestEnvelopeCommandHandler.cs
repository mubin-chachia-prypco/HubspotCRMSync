using Application.Commands.IngestCommands;
using Application.IServices;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.CommandHandlers.IngestCommandHandlers
{
    public class IngestEnvelopeCommandHandler : IRequestHandler<IngestEnvelopeCommand, IngestEnvelopeResult>
    {
        private readonly IQueueMessageProducer _producer;
        private readonly ILogger<IngestEnvelopeCommandHandler> _logger;

        public IngestEnvelopeCommandHandler(IQueueMessageProducer producer, ILogger<IngestEnvelopeCommandHandler> logger)
        {
            _producer = producer;
            _logger = logger;
        }

        public async Task<IngestEnvelopeResult> Handle(IngestEnvelopeCommand request, CancellationToken cancellationToken)
        {
            var message = request.Message;
            _logger.LogDebug("Enqueuing HubSpot sync envelope '{Key}' ({ObjectType}).", message.IdempotencyKey, message.ObjectType);

            await _producer.EnqueueHubSpotSyncAsync(message, cancellationToken);

            _logger.LogInformation("HubSpot sync envelope '{Key}' queued.", message.IdempotencyKey);
            return new IngestEnvelopeResult(message.IdempotencyKey);
        }
    }
}
