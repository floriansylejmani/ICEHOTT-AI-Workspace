using ICEHOTT.Domain.Security;

namespace ICEHOTT.Application.Abstractions;

public interface IRefreshSessionRepository
{
    Task<RefreshSession?> FindActiveByHashAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task AddAsync(RefreshSession session, CancellationToken cancellationToken = default);
}
