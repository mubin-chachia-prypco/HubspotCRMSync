using MediatR;

namespace Application.Commands.InboundLeadCommands
{
    /// <summary>
    /// Store a partner (Dubizzle/Bayut) lead payload opaquely and return its one-time token
    /// (the UUIDv7 the FE redeems to prefill the calculator). Store-only — no HubSpot write. Spec §15.
    /// </summary>
    public class CreateInboundLeadCommand : IRequest<Guid>
    {
        public required string Source { get; init; }

        /// <summary>Raw partner payload, already validated as JSON by the caller; stored verbatim as jsonb.</summary>
        public required string PayloadJson { get; init; }
    }
}
