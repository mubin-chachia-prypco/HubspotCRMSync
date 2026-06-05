using System.Globalization;

namespace HubSpotLeadSync;

/// <summary>
/// Safety net for changes HubSpot webhooks don't emit (some property changes, lifecycle
/// transitions, bulk imports). Periodically asks "what changed since the last sweep?".
/// </summary>
public sealed class ReconciliationWorker(IServiceProvider sp, ILogger<ReconciliationWorker> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);

                using var scope = sp.CreateScope();
                var hs = scope.ServiceProvider.GetRequiredService<HubSpotClient>();
                var mirror = scope.ServiceProvider.GetRequiredService<LocalMirror>();
                foreach (var objectType in new[] { "contacts", "deals" })
                {
                    var ids = await hs.SearchChangedSinceAsync(objectType, since, stoppingToken);
                    if (ids.Count == 0) continue;
                    log.LogInformation("Reconciliation: {Count} {Type} changed since last sweep", ids.Count, objectType);

                    // Pull the HubSpot-owned properties for the changed objects and apply them to the
                    // local mirror — same ownership/last-writer rules as the webhook path. Caps the
                    // scope to what HubSpot owns; ids without a local opportunity are skipped by LocalMirror.
                    var owned = HubSpotOwnedFields.For(objectType);
                    var props = owned.Append("hs_lastmodifieddate").ToArray();
                    foreach (var (id, values) in await hs.BatchReadAsync(objectType, ids, props, stoppingToken))
                    {
                        var occurredAt = ParseHsDate(values.GetValueOrDefault("hs_lastmodifieddate"));
                        foreach (var prop in owned)
                            mirror.ApplyChange(objectType, id, prop, values.GetValueOrDefault(prop), occurredAt);
                    }
                }
                since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogError(ex, "Reconciliation sweep failed"); }
        }
    }

    /// <summary>Parse hs_lastmodifieddate, which comes back as epoch-ms or ISO-8601 depending on the API surface.</summary>
    private static DateTimeOffset ParseHsDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTimeOffset.UtcNow;
        if (long.TryParse(value, out var ms)) return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt : DateTimeOffset.UtcNow;
    }
}
