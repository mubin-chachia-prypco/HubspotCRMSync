using System.Text.Json;
using Application.IRepositories;
using Domain.Entities.Outbox;

namespace Infrastructure.Persistence.Repositories
{
    /// <summary>
    /// EF Core implementation of <see cref="IOutboxRepository"/>. Adds the message to the
    /// <see cref="AppDbContext"/> change tracker so it flushes alongside the caller's domain
    /// writes when they call <c>SaveChangesAsync</c>. Does not save on its own.
    /// (Copied from InstaMortgageService for parity.)
    /// </summary>
    public class OutboxRepository : IOutboxRepository
    {
        private readonly AppDbContext _appDbContext;

        public OutboxRepository(AppDbContext appDbContext)
        {
            _appDbContext = appDbContext;
        }

        public async Task EnqueueAsync<T>(string type, T payload, CancellationToken cancellationToken)
            where T : class
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(type);
            ArgumentNullException.ThrowIfNull(payload);

            var message = new OutboxMessage
            {
                Type = type,
                Payload = JsonSerializer.Serialize(payload),
            };

            await _appDbContext.OutboxMessages.AddAsync(message, cancellationToken);
        }
    }
}
