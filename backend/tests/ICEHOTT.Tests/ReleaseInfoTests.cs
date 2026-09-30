using System.Net.Http.Json;
using System.Text.Json;
using ICEHOTT.API.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace ICEHOTT.Tests;

public sealed class ReleaseInfoTests
{
    private const string Sha = "c63a6b2337a1e17c056d5adbefc1daba83e8abf1";

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(x => x.Key, x => x.Value))
            .Build();

    [Fact]
    public void Projects_Configured_Git_Sha_Tier_And_Role()
    {
        var info = ReleaseInfo.Create(
            Config(("Release:GitSha", Sha), ("Deployment:Tier", "staging")),
            "Staging", ServiceRole.Worker);

        Assert.Equal("icehott-worker", info.Service);
        Assert.Equal("Worker", info.Role);
        Assert.Equal("staging", info.Environment);
        Assert.Equal(Sha, info.GitSha);
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
    }

    [Theory]
    [InlineData(ServiceRole.Api, "icehott-api")]
    [InlineData(ServiceRole.All, "icehott-api")]
    [InlineData(ServiceRole.Worker, "icehott-worker")]
    public void Service_Name_Follows_Role(ServiceRole role, string expected) =>
        Assert.Equal(expected, ReleaseInfo.Create(Config(), "Development", role).Service);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("zzzzzzz")]
    [InlineData("c63a6b2; rm -rf /")]
    [InlineData("c63a6b")]
    public void Invalid_Or_Missing_Sha_Is_Reported_As_Unknown(string? sha)
    {
        var info = ReleaseInfo.Create(Config(("Release:GitSha", sha)), "Production", ServiceRole.Api);
        Assert.Equal("unknown", info.GitSha);
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("production")]
    public void Hosted_Tiers_Only_Report_A_Full_Sha(string tier)
    {
        var shortSha = ReleaseInfo.Create(
            Config(("Release:GitSha", "c63a6b2"), ("Deployment:Tier", tier)),
            "Production", ServiceRole.Api);
        Assert.Equal("unknown", shortSha.GitSha);

        var full = ReleaseInfo.Create(
            Config(("Release:GitSha", Sha), ("Deployment:Tier", tier)),
            "Production", ServiceRole.Api);
        Assert.Equal(Sha, full.GitSha);
    }

    [Fact]
    public void Local_Builds_May_Report_An_Abbreviated_Sha() =>
        Assert.Equal(
            "c63a6b2",
            ReleaseInfo.Create(
                Config(("Release:GitSha", "c63a6b2")), "Development", ServiceRole.Api).GitSha);

    [Theory]
    [InlineData("c63a6b2337a1e17c056d5adbefc1daba83e8abf1", true)]
    [InlineData("C63A6B2337A1E17C056D5ADBEFC1DABA83E8ABF1", true)]
    [InlineData("c63a6b2", false)]
    [InlineData("c63a6b2337a1e17c056d5adbefc1daba83e8abf12", false)]
    [InlineData(null, false)]
    public void Full_Sha_Means_Exactly_Forty_Hex_Characters(string? value, bool expected) =>
        Assert.Equal(expected, ReleaseInfo.IsFullSha(value));

    [Fact]
    public void Sha_Is_Normalised_To_Lower_Case() =>
        Assert.Equal(
            Sha,
            ReleaseInfo.Create(
                Config(("Release:GitSha", Sha.ToUpperInvariant())),
                "Production", ServiceRole.Api).GitSha);

    [Fact]
    public void Environment_Falls_Back_To_Aspnet_Environment() =>
        Assert.Equal(
            "Development",
            ReleaseInfo.Create(Config(), "Development", ServiceRole.Api).Environment);

    [Fact]
    public async Task Release_Endpoint_Exposes_Only_Safe_Fields()
    {
        using var factory = new IcehottApiFactory(b =>
        {
            b.UseSetting("Release:GitSha", Sha);
            b.UseSetting("Deployment:Tier", "local");
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/release");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(Sha, json.GetProperty("gitSha").GetString());
        Assert.Equal("local", json.GetProperty("environment").GetString());
        Assert.Equal("icehott-api", json.GetProperty("service").GetString());
        Assert.Equal(
            new[] { "environment", "gitSha", "role", "service", "version" },
            json.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));

        var body = json.GetRawText();
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Key", body);
    }
}
