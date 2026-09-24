namespace ICEHOTT.Domain.Users;

public sealed class User
{
    private User() { }

    public User(Guid id, string email, string displayName, string passwordHash, DateTimeOffset createdAtUtc)
    {
        Id = id;
        Email = email.Trim();
        NormalizedEmail = Email.ToUpperInvariant();
        DisplayName = displayName.Trim();
        PasswordHash = passwordHash;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string NormalizedEmail { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public void SetPasswordHash(string passwordHash)
    {
        PasswordHash = passwordHash;
    }
}
