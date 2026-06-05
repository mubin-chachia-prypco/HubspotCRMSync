using System.Collections.Concurrent;

namespace HubSpotLeadSync;

// --- Opportunity store (our DB seam: maps an opportunity to its HubSpot ids) ---
public interface IOpportunityStore
{
    OpportunityRecord? GetByOpportunityId(string opportunityId);
    OpportunityRecord? GetOpenForCustomer(string customerId);
    void Save(OpportunityRecord record);
}

public sealed class InMemoryOpportunityStore : IOpportunityStore
{
    private readonly ConcurrentDictionary<string, OpportunityRecord> _byId = new();

    public OpportunityRecord? GetByOpportunityId(string opportunityId) =>
        _byId.TryGetValue(opportunityId, out var r) ? r : null;

    public OpportunityRecord? GetOpenForCustomer(string customerId) =>
        _byId.Values.FirstOrDefault(r => r.CustomerId == customerId && r.State == OpportunityState.Open);

    public void Save(OpportunityRecord record)
    {
        record.UpdatedAt = DateTimeOffset.UtcNow;
        _byId[record.OpportunityId] = record;
    }
}

// --- Transactional outbox (in real life this is a DB table written in the same txn as the lead) ---
public interface IOutbox
{
    void Enqueue(OutboxMessage message);
    IReadOnlyList<OutboxMessage> DequeueBatch(int max);
    void Requeue(OutboxMessage message);
}

public sealed class InMemoryOutbox : IOutbox
{
    private readonly ConcurrentQueue<OutboxMessage> _q = new();
    public void Enqueue(OutboxMessage message) => _q.Enqueue(message);
    public void Requeue(OutboxMessage message) => _q.Enqueue(message);

    public IReadOnlyList<OutboxMessage> DequeueBatch(int max)
    {
        var list = new List<OutboxMessage>();
        while (list.Count < max && _q.TryDequeue(out var m)) list.Add(m);
        return list;
    }
}

// --- Idempotency: process each webhook eventId at most once ---
public interface IProcessedEvents { bool TryMarkProcessed(long eventId); }

public sealed class InMemoryProcessedEvents : IProcessedEvents
{
    private readonly ConcurrentDictionary<long, byte> _seen = new();
    public bool TryMarkProcessed(long eventId) => _seen.TryAdd(eventId, 1);
}

// --- Echo guard: suppress inbound webhooks caused by our own writes ---
public interface IEchoGuard
{
    void MarkWritten(string objectType, string objectId);
    bool WasWrittenByUsRecently(string objectType, string objectId);
}

public sealed class InMemoryEchoGuard : IEchoGuard
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(120);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _writes = new();
    private static string Key(string t, string id) => $"{t}:{id}";

    public void MarkWritten(string objectType, string objectId) =>
        _writes[Key(objectType, objectId)] = DateTimeOffset.UtcNow;

    public bool WasWrittenByUsRecently(string objectType, string objectId) =>
        _writes.TryGetValue(Key(objectType, objectId), out var ts) && DateTimeOffset.UtcNow - ts < Ttl;
}
