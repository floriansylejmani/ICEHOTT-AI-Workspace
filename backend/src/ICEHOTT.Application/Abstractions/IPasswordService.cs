using ICEHOTT.Domain.Users;

namespace ICEHOTT.Application.Abstractions;

public interface IPasswordService
{
    string Hash(User user, string password);
    bool Verify(User user, string passwordHash, string password);
}
