namespace HubSpotLeadSync;

/// <summary>
/// Processes inbound HubSpot webhook events. Three guards:
///   - Idempotency: each eventId is handled at most once.
///   - Echo-suppression: ignore changes our own integration made (changeSource = INTEGRATION,
///     or an object we wrote in the last couple of minutes) to avoid sync loops.
///   - Ownership: HubSpot owns top-of-funnel / qualification, so we apply those to our copy.
///     Application/processing data is owned by Pulse and ignored here.
/// </summary>
public sealed class InboundEventProcessor(IProcessedEvents processed, IEchoGuard echo, ILogger<InboundEventProcessor> log)
{
    public Task ProcessAsync(IEnumerable<WebhookEvent> events, CancellationToken ct = default)
    {
        foreach (var e in events)
        {
            if (ct.IsCancellationRequested) break;

            if (!processed.TryMarkProcessed(e.EventId))
                continue; // already handled

            var objectType = e.SubscriptionType.StartsWith("deal", StringComparison.OrdinalIgnoreCase) ? "deals" : "contacts";

            if (string.Equals(e.ChangeSource, "INTEGRATION", StringComparison.OrdinalIgnoreCase)
                || echo.WasWrittenByUsRecently(objectType, e.ObjectId.ToString()))
            {
                log.LogDebug("Skipping echo: {Sub} {Id} (source={Src})", e.SubscriptionType, e.ObjectId, e.ChangeSource);
                continue;
            }

            // PoC: log the genuine external change. A real impl updates the local mirror for
            // HubSpot-owned fields and ignores anything owned by Pulse.
            log.LogInformation("Applying HubSpot change: {Sub} {Id} {Prop}={Val} (source={Src})",
                e.SubscriptionType, e.ObjectId, e.PropertyName, e.PropertyValue, e.ChangeSource);
        }
        return Task.CompletedTask;
    }
}
