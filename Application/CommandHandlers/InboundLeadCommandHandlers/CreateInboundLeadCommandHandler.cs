using Application.Commands.InboundLeadCommands;
using Application.IRepositories;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.CommandHandlers.InboundLeadCommandHandlers
{
    public class CreateInboundLeadCommandHandler : IRequestHandler<CreateInboundLeadCommand, Guid>
    {
        // Short-lived prefill handoff — the token is one-time and expires quickly (spec §15).
        private static readonly TimeSpan TokenTtl = TimeSpan.FromSeconds(60);

        private readonly IInboundLeadRepository _leads;
        private readonly ILogger<CreateInboundLeadCommandHandler> _logger;

        public CreateInboundLeadCommandHandler(IInboundLeadRepository leads, ILogger<CreateInboundLeadCommandHandler> logger)
        {
            _leads = leads;
            _logger = logger;
        }

        public async Task<Guid> Handle(CreateInboundLeadCommand request, CancellationToken cancellationToken)
        {
            var lead = await _leads.CreateAsync(request.Source, request.PayloadJson, TokenTtl, cancellationToken);
            _logger.LogInformation("Stored inbound '{Source}' lead {Token}.", request.Source, lead.Id);
            return lead.Id;
        }
    }
}
