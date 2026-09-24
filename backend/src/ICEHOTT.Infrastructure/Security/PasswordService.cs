using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Users;
using Microsoft.AspNetCore.Identity;

namespace ICEHOTT.Infrastructure.Security;

public sealed class PasswordService : IPasswordService
{
    private readonly PasswordHasher<User> _hasher = new();

    public string Hash(User user, string password) => _hasher.HashPassword(user, password);

    public bool Verify(User user, string passwordHash, string password) =>
        _hasher.VerifyHashedPassword(user, passwordHash, password) != PasswordVerificationResult.Failed;
}
