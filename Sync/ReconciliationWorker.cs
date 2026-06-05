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
                foreach (var objectType in new[] { "contacts", "deals" })
                {
                    var ids = await hs.SearchChangedSinceAsync(objectType, since, stoppingToken);
                    if (ids.Count > 0)
                        log.LogInformation("Reconciliation: {Count} {Type} changed since last sweep", ids.Count, objectType);
                    // PoC: a real impl reconciles each id against the local mirror.
                }
                since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogError(ex, "Reconciliation sweep failed"); }
        }
    }
}
