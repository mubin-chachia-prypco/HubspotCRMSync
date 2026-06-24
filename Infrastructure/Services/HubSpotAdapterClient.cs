using System.Net.Http.Json;
using Application.IServices;
using Application.Messages;
using Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services
{
    /// <summary>
    /// Posts the generic envelope to the HubSpot adapter API (TypeScript + Fastify, on Kubernetes).
    /// Authenticates with the shared service token, sent as the <c>X-AI-Agent-Key</c> header — the
    /// service-to-service path the adapter accepts (it also accepts an Ory session, but that's for
    /// user-facing callers). An empty token means no auth header (e.g. local dev).
    /// </summary>
    public class HubSpotAdapterClient : IHubSpotAdapterClient
    {
        private const string ServiceTokenHeader = "X-AI-Agent-Key";

        private readonly HttpClient _http;
        private readonly ILogger<HubSpotAdapterClient> _logger;
        private readonly HubSpotSyncSettings _settings;

        public HubSpotAdapterClient(
            HttpClient http,
            ILogger<HubSpotAdapterClient> logger,
            IOptionsMonitor<HubSpotSyncSettings> settings)
        {
            _http = http;
            _logger = logger;
            _settings = settings.CurrentValue;
        }

        public async Task<bool> SendAsync(HubSpotSyncMessage message, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _settings.AdapterIngestUrl)
            {
                Content = JsonContent.Create(message)
            };

            if (!string.IsNullOrWhiteSpace(_settings.AdapterServiceToken))
                request.Headers.Add(ServiceTokenHeader, _settings.AdapterServiceToken);

            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return true;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("HubSpot adapter call failed {Status}: {Body}", (int)response.StatusCode, body);
            return false;
        }

        public async Task<string> GetEnumsAsync(CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _settings.AdapterEnumsUrl);
            if (!string.IsNullOrWhiteSpace(_settings.AdapterServiceToken))
                request.Headers.Add(ServiceTokenHeader, _settings.AdapterServiceToken);

            using var response = await _http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
    }
}
