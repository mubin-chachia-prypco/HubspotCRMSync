using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HubSpotLeadSync;

public sealed class HubSpotOptions
{
    public string AccessToken { get; set; } = "";
    public string ClientSecret { get; set; } = "";   // app secret, for webhook v3 signature validation
    public string BaseUrl { get; set; } = "https://api.hubapi.com";
}

/// <summary>
/// Thin wrapper over the HubSpot CRM v3/v4 REST API. No official C# client exists and the
/// community one is unmaintained, so we talk to the API directly. Handles auth and
/// 429 / 5xx retry with back-off.
/// </summary>
public sealed partial class HubSpotClient
{
    private const int MaxAttempts = 5;
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    public HubSpotClient(HttpClient http, HubSpotOptions options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.BaseUrl);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
    }

    public async Task<string?> FindIdByPropertyAsync(string objectType, string property, string value, CancellationToken ct)
    {
        var body = new
        {
            filterGroups = new[] { new { filters = new[] { new { propertyName = property, @operator = "EQ", value } } } },
            properties = new[] { property },
            limit = 1
        };
        using var resp = await SendAsync(() => Json(HttpMethod.Post, $"/crm/v3/objects/{objectType}/search", body), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var results = doc.RootElement.GetProperty("results");
        return results.GetArrayLength() == 0 ? null : results[0].GetProperty("id").GetString();
    }

    public async Task<string> CreateAsync(string objectType, IReadOnlyDictionary<string, string> props, CancellationToken ct)
    {
        using var resp = await SendAsync(() => Json(HttpMethod.Post, $"/crm/v3/objects/{objectType}", new { properties = props }), ct);
        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            var id = ExtractExistingId(await resp.Content.ReadAsStringAsync(ct));
            if (id is not null) throw new ContactAlreadyExistsException(id);
        }
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    public async Task UpdateAsync(string objectType, string id, IReadOnlyDictionary<string, string> props, CancellationToken ct)
    {
        using var resp = await SendAsync(() => Json(HttpMethod.Patch, $"/crm/v3/objects/{objectType}/{id}", new { properties = props }), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task AssociateDefaultAsync(string fromType, string fromId, string toType, string toId, CancellationToken ct)
    {
        var path = $"/crm/v4/objects/{fromType}/{fromId}/associations/default/{toType}/{toId}";
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, path), ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Reconciliation seam: ids changed since a unix-ms timestamp.</summary>
    public async Task<IReadOnlyList<string>> SearchChangedSinceAsync(string objectType, long sinceMs, CancellationToken ct)
    {
        var body = new
        {
            filterGroups = new[] { new { filters = new[] { new { propertyName = "hs_lastmodifieddate", @operator = "GT", value = sinceMs.ToString() } } } },
            properties = new[] { "hs_lastmodifieddate" },
            limit = 100
        };
        using var resp = await SendAsync(() => Json(HttpMethod.Post, $"/crm/v3/objects/{objectType}/search", body), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var ids = new List<string>();
        foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
            ids.Add(r.GetProperty("id").GetString()!);
        return ids;
    }

    private static HttpRequestMessage Json(HttpMethod m, string path, object body) =>
        new(m, path) { Content = JsonContent.Create(body, options: J) };

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var req = factory();
            var resp = await _http.SendAsync(req, ct);
            var retryable = resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
            if (!retryable || attempt >= MaxAttempts) return resp;
            var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
            resp.Dispose();
            await Task.Delay(delay, ct);
        }
    }

    private static string? ExtractExistingId(string message)
    {
        var m = ExistingIdRegex().Match(message);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"Existing ID:\s*(\d+)")]
    private static partial Regex ExistingIdRegex();
}

public sealed class ContactAlreadyExistsException(string existingId) : Exception
{
    public string ExistingId { get; } = existingId;
}
