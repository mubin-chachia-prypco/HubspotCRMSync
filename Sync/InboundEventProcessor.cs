namespace HubSpotLeadSync;

/// <summary>
/// Processes inbound HubSpot webhook events. Three guards:
///   - Idempotency: each eventId is handled at most once.
///   - Echo-suppression: ignore changes our own integration made (changeSource = INTEGRATION,
///     or an object we wrote in the last couple of minutes) to avoid sync loops.
///   - Ownership: HubSpot owns top-of-funnel / qualification, so we apply those to our copy.
///     Application/processing data is owned by Pulse and ignored here.
/// </summary>
public sealed class InboundEventProcessor(IProcessedEvents processed, IEchoGuard echo, LocalMirror mirror, ILogger<InboundEventProcessor> log)
{
    public Task ProcessAsync(IEnumerable<WebhookEvent> events, CancellationToken ct = default)
    {
        foreach (var e in events)
        {
            if (ct.IsCancellationRequested) break;

            if (!processed.TryMarkProcessed(e.EventId))
                continue; // already handled

            var objectType = ResolveObjectType(e);

            if (string.Equals(e.ChangeSource, "INTEGRATION", StringComparison.OrdinalIgnoreCase)
                || echo.WasWrittenByUsRecently(objectType, e.ObjectId.ToString()))
            {
                log.LogDebug("Skipping echo: {Sub} {Id} (source={Src})", e.SubscriptionType, e.ObjectId, e.ChangeSource);
                continue;
            }

            // Apply the change to our local mirror. LocalMirror enforces the ownership rule
            // (only HubSpot-owned top-of-funnel fields are mirrored; Pulse-owned data is ignored)
            // and the last-writer rule (stale events are dropped).
            var occurredAt = DateTimeOffset.FromUnixTimeMilliseconds(e.OccurredAt);
            mirror.ApplyChange(objectType, e.ObjectId.ToString(), e.PropertyName, e.PropertyValue, occurredAt);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolve our object type ("deals"/"contacts") from either payload shape: the new generic
    /// (crmObjects) payload carries <c>objectTypeId</c> (0-3 deal, 0-1 contact); the legacy payload
    /// encodes the type in <c>subscriptionType</c> ("deal.*" / "contact.*").
    /// </summary>
    private static string ResolveObjectType(WebhookEvent e)
    {
        if (!string.IsNullOrEmpty(e.ObjectTypeId))
            return e.ObjectTypeId == "0-3" ? "deals" : "contacts";
        return e.SubscriptionType.StartsWith("deal", StringComparison.OrdinalIgnoreCase) ? "deals" : "contacts";
    }
}
