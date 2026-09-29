using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace ICEHOTT.Tests;

/// <summary>
/// The worker exposes no controllers, so it must not need browser (CORS) or token-signing
/// configuration just to start, while the API role keeps both requirements.
/// "Testing" is neither Development nor a hosted environment, so the generic Program
/// checks (not the hosted validator) are what is exercised here.
/// </summary>
public sealed class WorkerRoleStartupTests
{
    private static void Common(IWebHostBuilder builder, string role)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Service:Role", role);
        builder.UseSetting(
            "ConnectionStrings:DefaultConnection",
            "Host=db.internal;Database=icehott;Username=app;Password=Not-A-Real-Pw-1");
    }

    [Fact]
    public async Task Worker_Starts_Without_Cors_Origins_Or_Jwt_Key()
    {
        using var factory = new IcehottApiFactory(b => Common(b, "Worker"));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/release")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsync("/api/auth/login", null)).StatusCode);
    }

    [Fact]
    public void Api_Still_Requires_Cors_Origins_Outside_Development()
    {
        var exception = Assert.ThrowsAny<Exception>(() =>
            new IcehottApiFactory(b =>
            {
                Common(b, "Api");
                b.UseSetting("Jwt:Key", "worker-test-jwt-key-with-at-least-32-characters");
            }));
        Assert.Contains("Cors:AllowedOrigins", Flatten(exception));
    }

    [Fact]
    public void Api_Still_Requires_A_Strong_Jwt_Key()
    {
        var exception = Assert.ThrowsAny<Exception>(() =>
            new IcehottApiFactory(b =>
            {
                Common(b, "Api");
                b.UseSetting("Cors:AllowedOrigins:0", "https://app.test");
            }));
        Assert.Contains("Jwt:Key", Flatten(exception));
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            messages.Add(current.Message);
            if (current.InnerException is null) break;
        }
        return string.Join(" | ", messages);
    }
}
