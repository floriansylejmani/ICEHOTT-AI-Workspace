using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Security;
using ICEHOTT.Domain.Users;

namespace ICEHOTT.Application.Auth;

public sealed class AuthService(
    IUserRepository users,
    IRefreshSessionRepository sessions,
    IUnitOfWork unitOfWork,
    IPasswordService passwords,
    ITokenService tokens,
    TimeProvider clock)
{
    public async Task<AuthResult> RegisterAsync(string email, string displayName, string password, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = email.Trim().ToUpperInvariant();
        if (await users.FindByNormalizedEmailAsync(normalizedEmail, cancellationToken) is not null)
            return new(null, "email_exists");

        var now = clock.GetUtcNow();
        var user = new User(Guid.NewGuid(), email, displayName, string.Empty, now);
        user.SetPasswordHash(passwords.Hash(user, password));

        await users.AddAsync(user, cancellationToken);
        var session = await IssueSessionAsync(user, now, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new(session, null);
    }

    public async Task<AuthResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByNormalizedEmailAsync(email.Trim().ToUpperInvariant(), cancellationToken);
        if (user is null || !passwords.Verify(user, user.PasswordHash, password))
            return new(null, "invalid_credentials");

        var session = await IssueSessionAsync(user, clock.GetUtcNow(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new(session, null);
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var hash = tokens.HashRefreshToken(refreshToken);
        var current = await sessions.FindActiveByHashAsync(hash, now, cancellationToken);
        if (current is null)
            return new(null, "invalid_refresh_token");

        var replacementId = Guid.NewGuid();
        current.Revoke(now, replacementId);
        var session = await IssueSessionAsync(current.User, now, cancellationToken, replacementId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new(session, null);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var session = await sessions.FindActiveByHashAsync(tokens.HashRefreshToken(refreshToken), now, cancellationToken);
        if (session is null) return;

        session.Revoke(now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<AuthSession> IssueSessionAsync(User user, DateTimeOffset now, CancellationToken cancellationToken, Guid? sessionId = null)
    {
        var access = tokens.CreateAccessToken(user);
        var refreshToken = tokens.CreateRefreshToken();
        var refresh = new RefreshSession(
            sessionId ?? Guid.NewGuid(),
            user.Id,
            tokens.HashRefreshToken(refreshToken),
            now,
            now.Add(tokens.RefreshLifetime));

        await sessions.AddAsync(refresh, cancellationToken);
        return new(
            new AuthUser(user.Id, user.Email, user.DisplayName),
            access.Value,
            access.ExpiresAtUtc,
            refreshToken);
    }
}
