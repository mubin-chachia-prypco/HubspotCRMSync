using Application.IServices;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.Queries.EnumQueries
{
    /// <summary>
    /// Returns the adapter's generic enum vocabulary (raw JSON) for the FE. The FE talks only to this
    /// service (rolled into instamortgage); the CRM-specific enum values stay inside the adapter.
    /// </summary>
    public class GetAdapterEnumsQuery : IRequest<string>
    {
    }

    public class GetAdapterEnumsQueryHandler : IRequestHandler<GetAdapterEnumsQuery, string>
    {
        private readonly IHubSpotAdapterClient _adapter;
        private readonly ILogger<GetAdapterEnumsQueryHandler> _logger;

        public GetAdapterEnumsQueryHandler(IHubSpotAdapterClient adapter, ILogger<GetAdapterEnumsQueryHandler> logger)
        {
            _adapter = adapter;
            _logger = logger;
        }

        public async Task<string> Handle(GetAdapterEnumsQuery request, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Fetching enum vocabulary from the HubSpot adapter.");
            return await _adapter.GetEnumsAsync(cancellationToken);
        }
    }
}
