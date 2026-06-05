using System.Text.Json;

namespace HubSpotLeadSync;

/// <summary>Drains the outbox and pushes each change to HubSpot, off the request path.</summary>
public sealed class OutboxWorker(IOutbox outbox, IServiceProvider sp, ILogger<OutboxWorker> log) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(500);
    private const int MaxAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var batch = outbox.DequeueBatch(10);
            if (batch.Count == 0)
            {
                try { await Task.Delay(Idle, stoppingToken); } catch (OperationCanceledException) { break; }
                continue;
            }

            foreach (var msg in batch)
            {
                try
                {
                    using var scope = sp.CreateScope();
                    var sync = scope.ServiceProvider.GetRequiredService<LeadSyncService>();
                    var req = JsonSerializer.Deserialize<LeadSyncRequest>(msg.PayloadJson)!;
                    var res = await sync.SyncAsync(req, stoppingToken);
                    log.LogInformation("Synced {Opp}: contact={Contact} deal={Deal} held={Held}",
                        req.OpportunityId, res.ContactId, res.DealId, res.Held);
                }
                catch (Exception ex)
                {
                    msg.Attempts++;
                    if (msg.Attempts < MaxAttempts) outbox.Requeue(msg);
                    else log.LogError(ex, "Outbox message {Id} failed after {Attempts} attempts", msg.Id, msg.Attempts);
                }
            }
        }
    }
}
