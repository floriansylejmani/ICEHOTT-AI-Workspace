using System.Reflection;
using System.Text.RegularExpressions;

namespace ICEHOTT.API.Hosting;

/// <summary>Safe, non-secret release identity exposed at <c>GET /release</c>.</summary>
public sealed record ReleaseInfo(
    string Service,
    string Role,
    string Environment,
    string GitSha,
    string Version)
{
    public const string UnknownSha = "unknown";

    private static readonly Regex ShaPattern =
        new("^[0-9a-fA-F]{7,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValidSha(string? value) =>
        value is not null && ShaPattern.IsMatch(value);

    public static ReleaseInfo Create(
        IConfiguration configuration,
        string aspNetEnvironment,
        ServiceRole role)
    {
        var rawSha = configuration["Release:GitSha"]?.Trim();
        var sha = IsValidSha(rawSha) ? rawSha!.ToLowerInvariant() : UnknownSha;

        var tier = configuration["Deployment:Tier"]?.Trim().ToLowerInvariant();
        var environment = string.IsNullOrEmpty(tier) ? aspNetEnvironment : tier;

        var informational = typeof(ReleaseInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational)
            ? "0.0.0"
            : informational.Split('+')[0];

        return new ReleaseInfo(
            role == ServiceRole.Worker ? "icehott-worker" : "icehott-api",
            role.ToString(),
            environment,
            sha,
            version);
    }
}
