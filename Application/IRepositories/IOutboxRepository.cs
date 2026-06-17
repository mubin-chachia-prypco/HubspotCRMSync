namespace Application.IRepositories
{
    /// <summary>
    /// Adds an outbox row to the current EF change tracker so it flushes alongside the caller's
    /// domain writes on <c>SaveChangesAsync</c>. Does not save on its own. Mirrors
    /// InstaMortgageService's IOutboxRepository.
    /// </summary>
    public interface IOutboxRepository
    {
        Task EnqueueAsync<T>(string type, T payload, CancellationToken cancellationToken) where T : class;
    }
}
