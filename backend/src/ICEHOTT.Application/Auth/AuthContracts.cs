namespace ICEHOTT.Application.Auth;

public sealed record AuthUser(Guid Id, string Email, string DisplayName);
public sealed record AuthSession(AuthUser User, string AccessToken, DateTimeOffset AccessTokenExpiresAtUtc, string RefreshToken);
public sealed record AuthResult(AuthSession? Session, string? ErrorCode)
{
    public bool Succeeded => Session is not null;
}
