using Application.Commands.IngestCommands;
using Application.Messages;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers
{
    /// <summary>
    /// Ingress for the CRM-agnostic envelope from the portal / instamortgage. Thin — it just dispatches
    /// the command; the HubSpot adapter (Azure Function) owns all HubSpot mapping.
    /// </summary>
    [ApiController]
    public class IngestController : ControllerBase
    {
        private readonly IMediator _mediator;
        private readonly ILogger<IngestController> _logger;

        public IngestController(IMediator mediator, ILogger<IngestController> logger)
        {
            _mediator = mediator;
            _logger = logger;
        }

        [HttpPost("/ingest")]
        public async Task<IActionResult> Ingest([FromBody] HubSpotSyncMessage message, CancellationToken ct)
        {
            _logger.LogDebug("Ingest received envelope ({ObjectType}/{Operation}).", message.ObjectType, message.Operation);

            var result = await _mediator.Send(new IngestEnvelopeCommand { Message = message }, ct);
            return Accepted($"/ingest/{result.IdempotencyKey}", new { result.IdempotencyKey, queued = true });
        }
    }
}
