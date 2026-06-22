using Application.IRepositories;
using Domain.Entities.InboundLeads;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Queries.InboundLeadQueries
{
    /// <summary>
    /// Redeem a one-time intake token → the stored lead, or null if the token is unknown, expired, or
    /// already consumed. Redeeming marks it consumed (one-time). Spec §15.
    /// </summary>
    public class RedeemInboundLeadQuery : IRequest<InboundLead?>
    {
        public required Guid Token { get; init; }
    }

    public class RedeemInboundLeadQueryHandler : IRequestHandler<RedeemInboundLeadQuery, InboundLead?>
    {
        private readonly IInboundLeadRepository _leads;
        private readonly ILogger<RedeemInboundLeadQueryHandler> _logger;

        public RedeemInboundLeadQueryHandler(IInboundLeadRepository leads, ILogger<RedeemInboundLeadQueryHandler> logger)
        {
            _leads = leads;
            _logger = logger;
        }

        public async Task<InboundLead?> Handle(RedeemInboundLeadQuery request, CancellationToken cancellationToken)
        {
            var lead = await _leads.RedeemAsync(request.Token, cancellationToken);
            if (lead is null)
                _logger.LogInformation("Intake token {Token} not redeemable (unknown/expired/consumed).", request.Token);
            return lead;
        }
    }
}
