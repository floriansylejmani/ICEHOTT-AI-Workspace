using ICEHOTT.Domain.Users;

namespace ICEHOTT.Domain.Security;

public sealed class RefreshSession
{
    private RefreshSession() { }

    public RefreshSession(Guid id, Guid userId, string tokenHash, DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc)
    {
        Id = id;
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }
    public Guid? ReplacedBySessionId { get; private set; }
    public User User { get; private set; } = null!;

    public void Revoke(DateTimeOffset when, Guid? replacementId = null)
    {
        RevokedAtUtc = when;
        ReplacedBySessionId = replacementId;
    }
}
