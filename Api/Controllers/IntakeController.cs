using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Commands.InboundLeadCommands;
using Application.Queries.InboundLeadQueries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers
{
    /// <summary>
    /// Partner (Dubizzle/Bayut) lead intake. Store-only: persist the raw payload opaquely and hand back
    /// a one-time, short-lived token the FE redeems to prefill the calculator. No HubSpot write here.
    /// See docs/forwarder-adapter-spec.md §15.
    /// </summary>
    [ApiController]
    public class IntakeController : ControllerBase
    {
        private readonly IMediator _mediator;
        private readonly IConfiguration _config;
        private readonly ILogger<IntakeController> _logger;

        public IntakeController(IMediator mediator, IConfiguration config, ILogger<IntakeController> logger)
        {
            _mediator = mediator;
            _config = config;
            _logger = logger;
        }

        [HttpPost("/intake/dubizzle")]
        public async Task<IActionResult> Create(CancellationToken ct)
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(raw))
                return BadRequest(new { error = "empty body" });

            // Auth: a static Bearer token Dubizzle sends in the Authorization header, held in our secrets
            // (Key Vault in prod). Enforced only when configured, so local dev needs none.
            var bearer = _config["Intake:Dubizzle:BearerToken"];
            if (!string.IsNullOrEmpty(bearer) && !IsValidBearer(Request, bearer))
                return Unauthorized();

            // Stored as jsonb — reject anything that isn't valid JSON.
            try { using var _ = JsonDocument.Parse(raw); }
            catch (JsonException) { return BadRequest(new { error = "payload must be valid JSON" }); }

            var token = await _mediator.Send(new CreateInboundLeadCommand { Source = "dubizzle", PayloadJson = raw }, ct);
            return Ok(new { token });
        }

        // FE redeems the token on the PRYPCO landing page. One-time + TTL: 410 if unknown/expired/consumed.
        [HttpGet("/intake/dubizzle/{token:guid}")]
        public async Task<IActionResult> Redeem(Guid token, CancellationToken ct)
        {
            var lead = await _mediator.Send(new RedeemInboundLeadQuery { Token = token }, ct);
            return lead is null
                ? StatusCode(StatusCodes.Status410Gone)
                : Content(lead.Payload, "application/json");
        }

        private static bool IsValidBearer(HttpRequest req, string expected)
        {
            if (!req.Headers.TryGetValue("Authorization", out var header)) return false;

            const string prefix = "Bearer ";
            var value = header.ToString();
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) return false;

            var a = Encoding.UTF8.GetBytes(value[prefix.Length..].Trim());
            var b = Encoding.UTF8.GetBytes(expected);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }
    }
}
