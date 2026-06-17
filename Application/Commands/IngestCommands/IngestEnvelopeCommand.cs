using Application.Messages;
using MediatR;

namespace Application.Commands.IngestCommands
{
    /// <summary>
    /// Accept a CRM-agnostic envelope from the portal / instamortgage and enqueue it for at-least-once
    /// delivery to the adapter. The producer never inspects HubSpot specifics (see the spec).
    /// </summary>
    public class IngestEnvelopeCommand : IRequest<IngestEnvelopeResult>
    {
        public required HubSpotSyncMessage Message { get; init; }
    }

    public record IngestEnvelopeResult(string IdempotencyKey);
}
