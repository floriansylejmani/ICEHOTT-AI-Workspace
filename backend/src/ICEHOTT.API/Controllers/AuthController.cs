using System.Security.Claims;
using ICEHOTT.API.Models;
using ICEHOTT.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ICEHOTT.API.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService auth, IHostEnvironment environment) : ControllerBase
{
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await auth.RegisterAsync(request.Email, request.DisplayName, request.Password, cancellationToken);
        if (!result.Succeeded) return Conflict(new { code = result.ErrorCode });
        return CompleteAuthentication(result.Session!);
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await auth.LoginAsync(request.Email, request.Password, cancellationToken);
        if (!result.Succeeded) return Unauthorized(new { code = result.ErrorCode });
        return CompleteAuthentication(result.Session!);
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        if (!Request.Cookies.TryGetValue("icehott_refresh", out var refreshToken) || string.IsNullOrWhiteSpace(refreshToken))
            return Unauthorized(new { code = "missing_refresh_token" });

        var result = await auth.RefreshAsync(refreshToken, cancellationToken);
        if (!result.Succeeded) return Unauthorized(new { code = result.ErrorCode });
        return CompleteAuthentication(result.Session!);
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me()
    {
        var id = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var email = User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
        var displayName = User.FindFirstValue("display_name") ?? string.Empty;
        return Ok(new MeResponse(id, email, displayName));
    }

    private IActionResult CompleteAuthentication(AuthSession session)
    {
        Response.Cookies.Append("icehott_refresh", session.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = !environment.IsDevelopment(),
            SameSite = environment.IsDevelopment() ? SameSiteMode.Lax : SameSiteMode.None,
            Path = "/api/auth",
            MaxAge = TimeSpan.FromDays(30)
        });

        return Ok(new AuthResponse(session.User, session.AccessToken, session.AccessTokenExpiresAtUtc));
    }
}
