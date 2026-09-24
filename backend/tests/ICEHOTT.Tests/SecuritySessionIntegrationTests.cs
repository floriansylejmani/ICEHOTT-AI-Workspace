using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ICEHOTT.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ICEHOTT.Tests;

public sealed class SecuritySessionIntegrationTests : IClassFixture<IcehottApiFactory>
{
    private const string Password = "StrongPassword123!";
    private readonly IcehottApiFactory _factory;

    public SecuritySessionIntegrationTests(IcehottApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Password_Is_Stored_As_Hash()
    {
        using var client = _factory.CreateClient();
        var email = $"hash-{Guid.NewGuid():N}@icehott.dev";
        var response = await client.PostAsJsonAsync("/api/auth/register", new { email, displayName = "Hash User", password = Password });
        response.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        var user = await db.Users.SingleAsync(x => x.NormalizedEmail == email.ToUpperInvariant());
        Assert.NotEqual(Password, user.PasswordHash);
        Assert.DoesNotContain(Password, user.PasswordHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_Rotates_Token_And_Rejects_Replay()
    {
        using var client = _factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"rotate-{Guid.NewGuid():N}@icehott.dev",
            displayName = "Rotate User",
            password = Password
        });
        register.EnsureSuccessStatusCode();

        var oldRefresh = ExtractRefreshToken(register);
        var payload = JsonDocument.Parse(await register.Content.ReadAsStringAsync());
        Assert.False(payload.RootElement.TryGetProperty("refreshToken", out _));

        var refresh = await client.PostAsync("/api/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

        using var replay = _factory.CreateClient();
        replay.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", $"icehott_refresh={oldRefresh}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    private static string ExtractRefreshToken(HttpResponseMessage response)
    {
        var header = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("icehott_refresh=", StringComparison.Ordinal));
        var pair = header.Split(';', 2)[0];
        return pair["icehott_refresh=".Length..];
    }
}
