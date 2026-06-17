using System.Net.Http.Headers;
using System.Net.Http.Json;
using Application.IServices;
using Application.Messages;
using Application.Options;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services
{
    /// <summary>
    /// Posts the generic envelope to the HubSpot adapter Function, authenticating with the app's
    /// Managed Identity (the same DefaultAzureCredential pattern InstaMortgageService uses for
    /// Key Vault). The Function is protected by Entra "Easy Auth"; we present a bearer token for
    /// its App ID URI scope. If no scope is configured (e.g. local dev) the call goes unauthenticated.
    /// </summary>
    public class HubSpotAdapterClient : IHubSpotAdapterClient
    {
        private readonly HttpClient _http;
        private readonly ILogger<HubSpotAdapterClient> _logger;
        private readonly HubSpotSyncSettings _settings;
        private readonly TokenCredential _credential;

        public HubSpotAdapterClient(
            HttpClient http,
            ILogger<HubSpotAdapterClient> logger,
            IOptionsMonitor<HubSpotSyncSettings> settings)
        {
            _http = http;
            _logger = logger;
            _settings = settings.CurrentValue;
            // DefaultAzureCredential resolves the Managed Identity in Azure and your dev login locally.
            _credential = new DefaultAzureCredential();
        }

        public async Task<bool> SendAsync(HubSpotSyncMessage message, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _settings.AdapterIngestUrl)
            {
                Content = JsonContent.Create(message)
            };

            if (!string.IsNullOrWhiteSpace(_settings.AdapterScope))
            {
                var token = await _credential.GetTokenAsync(
                    new TokenRequestContext(new[] { _settings.AdapterScope }), cancellationToken);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            }

            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return true;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("HubSpot adapter call failed {Status}: {Body}", (int)response.StatusCode, body);
            return false;
        }
    }
}
