using System.ComponentModel.DataAnnotations;
using ICEHOTT.Application.Auth;

namespace ICEHOTT.API.Models;

public sealed record RegisterRequest(
    [Required, EmailAddress, MaxLength(320)] string Email,
    [Required, MinLength(2), MaxLength(120)] string DisplayName,
    [Required, MinLength(12), MaxLength(128)] string Password);

public sealed record LoginRequest(
    [Required, EmailAddress, MaxLength(320)] string Email,
    [Required, MaxLength(128)] string Password);

public sealed record AuthResponse(AuthUser User, string AccessToken, DateTimeOffset AccessTokenExpiresAtUtc);
public sealed record MeResponse(Guid Id, string Email, string DisplayName);
