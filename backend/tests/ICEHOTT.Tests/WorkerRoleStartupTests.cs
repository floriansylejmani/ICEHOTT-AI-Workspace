using System.Net;
using ICEHOTT.API.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace ICEHOTT.Tests;

/// <summary>
/// The worker exposes no controllers, so it must not need browser (CORS) or token-signing
/// configuration just to start, while the API role keeps both requirements. The
/// requirement logic is tested on in-memory configuration so results do not depend on
/// ambient environment variables (CI exports Jwt__Key and Cors__* for other tests).
/// </summary>
public sealed class WorkerRoleStartupTests
{
    private const string StrongKey = "worker-test-jwt-key-with-at-least-32-characters";

    private static IConfiguration Config(string? jwtKey = null, string? origin = null)
    {
        var values = new Dictionary<string, string?>();
        if (jwtKey is not null) values["Jwt:Key"] = jwtKey;
        if (origin is not null) values["Cors:AllowedOrigins:0"] = origin;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Worker_Requires_Neither_Jwt_Key_Nor_Cors_Origins(bool development) =>
        Assert.Empty(ApiStartupRequirements.Validate(Config(), ServiceRole.Worker, development));

    [Fact]
    public void Api_Requires_Cors_Origins_Outside_Development()
    {
        var errors = ApiStartupRequirements.Validate(
            Config(jwtKey: StrongKey), ServiceRole.Api, isDevelopment: false);
        Assert.Contains(errors, x => x.StartsWith("Cors:AllowedOrigins"));
        Assert.DoesNotContain(errors, x => x.StartsWith("Jwt:Key"));
    }

    [Fact]
    public void Api_May_Omit_Cors_Origins_In_Development() =>
        Assert.Empty(ApiStartupRequirements.Validate(
            Config(jwtKey: StrongKey), ServiceRole.Api, isDevelopment: true));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    public void Api_Requires_A_Strong_Jwt_Key_In_Every_Environment(string? key)
    {
        foreach (var development in new[] { true, false })
        {
            var errors = ApiStartupRequirements.Validate(
                Config(jwtKey: key, origin: "https://app.test"), ServiceRole.Api, development);
            Assert.Contains(errors, x => x.StartsWith("Jwt:Key"));
        }
    }

    [Fact]
    public void Api_With_Key_And_Origin_Is_Valid() =>
        Assert.Empty(ApiStartupRequirements.Validate(
            Config(jwtKey: StrongKey, origin: "https://app.test"), ServiceRole.Api, false));

    [Fact]
    public void All_Role_Keeps_The_Api_Requirements() =>
        Assert.NotEmpty(ApiStartupRequirements.Validate(Config(), ServiceRole.All, false));

    [Fact]
    public async Task Worker_Host_Serves_Only_Health_And_Release_Outside_Development()
    {
        using var factory = new IcehottApiFactory(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("Service:Role", "Worker");
            b.UseSetting(
                "ConnectionStrings:DefaultConnection",
                "Host=db.internal;Database=icehott;Username=app;Password=Not-A-Real-Pw-1");
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/release")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsync("/api/auth/login", null)).StatusCode);
    }
}
