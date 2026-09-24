using ICEHOTT.Domain.Users;

namespace ICEHOTT.Application.Abstractions;

public interface ITokenService
{
    AccessToken CreateAccessToken(User user);
    string CreateRefreshToken();
    string HashRefreshToken(string refreshToken);
    TimeSpan RefreshLifetime { get; }
}

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAtUtc);
