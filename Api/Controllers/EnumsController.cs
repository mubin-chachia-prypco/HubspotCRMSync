using Application.Queries.EnumQueries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers
{
    /// <summary>
    /// Exposes the generic enum vocabulary to the FE. The FE talks only to this service; we proxy the
    /// adapter's CRM-agnostic /enums so the FE never hard-codes (or even sees) CRM-specific values.
    /// </summary>
    [ApiController]
    public class EnumsController : ControllerBase
    {
        private readonly IMediator _mediator;
        private readonly ILogger<EnumsController> _logger;

        public EnumsController(IMediator mediator, ILogger<EnumsController> logger)
        {
            _mediator = mediator;
            _logger = logger;
        }

        [HttpGet("/enums")]
        public async Task<IActionResult> GetEnums(CancellationToken ct)
        {
            try
            {
                var json = await _mediator.Send(new GetAdapterEnumsQuery(), ct);
                return Content(json, "application/json");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Failed to fetch enum vocabulary from the adapter.");
                return StatusCode(StatusCodes.Status502BadGateway, new { ok = false, error = "adapter enums unavailable" });
            }
        }
    }
}
