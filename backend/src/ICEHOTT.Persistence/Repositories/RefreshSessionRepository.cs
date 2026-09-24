using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class RefreshSessionRepository(ICEHOTTDbContext db) : IRefreshSessionRepository
{
    public async Task<RefreshSession?> FindActiveByHashAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var session = await db.RefreshSessions
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

        if (session is null || session.RevokedAtUtc is not null || session.ExpiresAtUtc <= now)
            return null;

        return session;
    }

    public async Task AddAsync(RefreshSession session, CancellationToken cancellationToken = default) =>
        await db.RefreshSessions.AddAsync(session, cancellationToken);
}
