using System.Security.Cryptography;
using System.Text;

namespace HubSpotLeadSync;

/// <summary>
/// Validates HubSpot webhook v3 signatures. Per HubSpot's spec:
///   1. Reject if the X-HubSpot-Request-Timestamp is more than 5 minutes old.
///   2. Build the string: requestMethod + requestUri + requestBody + timestamp.
///   3. HMAC-SHA256 it with the app CLIENT SECRET, then base64-encode.
///   4. Constant-time compare to the X-HubSpot-Signature-v3 header.
/// </summary>
public sealed class WebhookSignatureValidator(HubSpotOptions options)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    public bool IsValid(string method, string fullUri, string body, string? timestampHeader, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(signatureHeader) || !long.TryParse(timestampHeader, out var tsMs))
            return false;

        var ts = DateTimeOffset.FromUnixTimeMilliseconds(tsMs);
        if (DateTimeOffset.UtcNow - ts > MaxAge) return false;

        var baseString = $"{method}{fullUri}{body}{timestampHeader}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.ClientSecret));
        var computed = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(baseString)));

        var a = Encoding.UTF8.GetBytes(computed);
        var b = Encoding.UTF8.GetBytes(signatureHeader);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
