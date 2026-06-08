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
    public string ApplicationObjectTypeId { get; set; } = "";

    /// <summary>
    /// Maps our canonical/portal stage names (e.g. "qualified") to HubSpot's internal deal
    /// stage ids. HubSpot's API wants the internal id, not the human label. Record the ids
    /// from the sandbox pipeline (plan §10) and fill this in config. Unmapped values pass
    /// through unchanged (with a warning), so callers already sending internal ids still work.
    /// </summary>
    public Dictionary<string, string> DealStages { get; set; } = new();

    /// <summary>HubSpot internal stage ids that count as Closed (Won/Lost/Abandoned). Drives the
    /// open-deal-reuse rule and the inbound mirror's Open/Closed flag. Empty = treat all as Open.</summary>
    public List<string> ClosedDealStages { get; set; } = new();
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
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"HubSpot {objectType} create failed {(int)resp.StatusCode}: {body}");
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    public async Task UpdateAsync(string objectType, string id, IReadOnlyDictionary<string, string> props, CancellationToken ct)
    {
        using var resp = await SendAsync(() => Json(HttpMethod.Patch, $"/crm/v3/objects/{objectType}/{id}", new { properties = props }), ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"HubSpot {objectType} update {id} failed {(int)resp.StatusCode}: {body}");
        }
    }

    public async Task AssociateDefaultAsync(string fromType, string fromId, string toType, string toId, CancellationToken ct)
    {
        var path = $"/crm/v4/objects/{fromType}/{fromId}/associations/default/{toType}/{toId}";
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, path), ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// v4 associations: the ids of <paramref name="toType"/> records linked to a
    /// <paramref name="fromType"/> record. Direct GET (not the rate-limited Search endpoint),
    /// so it's safe to use to find a contact's existing deals.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAssociatedIdsAsync(string fromType, string fromId, string toType, CancellationToken ct)
    {
        var path = $"/crm/v4/objects/{fromType}/{fromId}/associations/{toType}";
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var ids = new List<string>();
        foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
            ids.Add(r.GetProperty("toObjectId").GetInt64().ToString());
        return ids;
    }

    /// <summary>Fetch a single object record with all its properties.</summary>
    public async Task<(string Id, IReadOnlyDictionary<string, string?> Props)?> GetObjectAsync(string objectType, string id, CancellationToken ct)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"/crm/v3/objects/{objectType}/{id}?allProperties=true"), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"HubSpot GET {objectType}/{id} failed {(int)resp.StatusCode}: {body}");
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var recordId = doc.RootElement.GetProperty("id").GetString()!;
        var props = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("properties", out var p))
            foreach (var prop in p.EnumerateObject())
                props[prop.Name] = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetString();
        return (recordId, props);
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
