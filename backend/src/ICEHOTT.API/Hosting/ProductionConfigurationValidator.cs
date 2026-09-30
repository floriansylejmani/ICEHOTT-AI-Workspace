using ICEHOTT.Infrastructure.Artifacts;
using Npgsql;

namespace ICEHOTT.API.Hosting;

public sealed class ProductionConfigurationException(string message)
    : InvalidOperationException(message);

/// <summary>
/// Fail-closed startup validation for staging and production. Every message names a
/// configuration key and a category of problem; none ever includes a configured value,
/// so failures are safe to print to logs and consoles.
/// </summary>
public static class ProductionConfigurationValidator
{
    private static readonly string[] JwtPlaceholderMarkers =
    [
        "development-only", "never-use-in-production", "ci-only", "replace-with",
        "change-me", "changeme", "placeholder", "example"
    ];

    private static readonly string[] WeakDatabasePasswords =
    [
        "change-me", "changeme", "postgres", "password", "icehott", "admin", "root", "example"
    ];

    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "::1", "0.0.0.0"];

    public static bool IsProductionLike(IConfiguration configuration, string environmentName) =>
        IsHostedEnvironmentName(environmentName) || IsHostedTier(Tier(configuration));

    public static void Enforce(IConfiguration configuration, string environmentName)
    {
        var errors = Validate(configuration, environmentName);
        if (errors.Count > 0)
            throw new ProductionConfigurationException(
                $"Production configuration validation failed ({errors.Count} issue(s)): " +
                string.Join(" ", errors));
    }

    public static IReadOnlyList<string> Validate(
        IConfiguration configuration,
        string environmentName)
    {
        if (!IsProductionLike(configuration, environmentName))
            return [];

        var errors = new List<string>();
        var tier = Tier(configuration);

        if (string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
            errors.Add("ASPNETCORE_ENVIRONMENT: Development is not permitted for a staging or production deployment.");
        if (!IsHostedTier(tier))
            errors.Add("Deployment:Tier: must be set to 'staging' or 'production'.");

        ValidateRole(configuration, errors, out var role);
        ValidateRelease(configuration, errors);
        ValidateDatabase(configuration, errors);
        ValidateAiRuntime(configuration, errors);
        ValidateArtifactStorage(configuration, errors);
        errors.AddRange(ObservabilityOptions.Validate(configuration, hosted: true));

        // Token signing, browser origins, host filtering and proxy trust only matter to the
        // process that serves the public HTTP API. The worker exposes none of that, so it is
        // neither required to hold the JWT signing key nor to know the web origins.
        if (role is not "worker")
        {
            ValidateJwt(configuration, errors);
            ValidateCors(configuration, errors);
            ValidateAllowedHosts(configuration, errors);
            ValidateForwardedHeaders(configuration, errors);
        }

        return errors;
    }

    private static string? Tier(IConfiguration configuration) =>
        configuration["Deployment:Tier"]?.Trim().ToLowerInvariant();

    private static bool IsHostedTier(string? tier) => tier is "staging" or "production";

    private static bool IsHostedEnvironmentName(string name) =>
        string.Equals(name, "Production", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Staging", StringComparison.OrdinalIgnoreCase);

    private static void ValidateRole(
        IConfiguration configuration, List<string> errors, out string? role)
    {
        role = configuration[ServiceRoles.SettingName]?.Trim().ToLowerInvariant();
        if (role is not ("api" or "worker"))
        {
            errors.Add("Service:Role: must be explicitly set to 'Api' or 'Worker' (not unset, 'All' or unknown).");
            role = null;
        }
    }

    private static void ValidateRelease(IConfiguration configuration, List<string> errors)
    {
        if (!ReleaseInfo.IsFullSha(configuration["Release:GitSha"]?.Trim()))
            errors.Add("Release:GitSha: must be the exact deployed commit SHA (40 hexadecimal characters).");
    }

    private static void ValidateJwt(IConfiguration configuration, List<string> errors)
    {
        var key = configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(key))
        {
            errors.Add("Jwt:Key: is not configured.");
            return;
        }

        if (key.Trim().Length < 32)
            errors.Add("Jwt:Key: is shorter than 32 characters.");
        if (JwtPlaceholderMarkers.Any(x => key.Contains(x, StringComparison.OrdinalIgnoreCase)))
            errors.Add("Jwt:Key: matches a known development or placeholder value.");
    }

    private static void ValidateDatabase(IConfiguration configuration, List<string> errors)
    {
        const string name = "ConnectionStrings:DefaultConnection";
        var connection = configuration[name];

        // Auto-migration is independent of the connection string, so always evaluate it.
        if (Flag(configuration, "Database:AutoMigrate", false, errors))
            errors.Add("Database:AutoMigrate: automatic schema migration is forbidden; migrate through the explicit release job.");

        if (string.IsNullOrWhiteSpace(connection))
        {
            errors.Add($"{name}: is not configured.");
            return;
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connection);
        }
        catch (Exception)
        {
            // Parser messages can echo fragments of the input; report the category only.
            errors.Add($"{name}: is not a valid PostgreSQL connection string.");
            return;
        }

        if (string.IsNullOrWhiteSpace(builder.Password) ||
            WeakDatabasePasswords.Contains(builder.Password, StringComparer.OrdinalIgnoreCase))
            errors.Add($"{name}: password is missing or is a known development default.");
        if (string.Equals(builder.Username, "postgres", StringComparison.OrdinalIgnoreCase))
            errors.Add($"{name}: must use a dedicated non-superuser application role.");
        if (string.IsNullOrWhiteSpace(builder.Host) ||
            LoopbackHosts.Contains(builder.Host.Trim(), StringComparer.OrdinalIgnoreCase))
            errors.Add($"{name}: host is missing or is a loopback address.");

        if (Flag(configuration, "Database:RequireTransportSecurity", true, errors) &&
            builder.SslMode is not (SslMode.Require or SslMode.VerifyCA or SslMode.VerifyFull))
            errors.Add($"{name}: transport security is required (SSL Mode must be Require, VerifyCA or VerifyFull).");
    }

    private static void ValidateAiRuntime(IConfiguration configuration, List<string> errors)
    {
        if (!Uri.TryCreate(configuration["AiRuntime:BaseUrl"], UriKind.Absolute, out var uri))
        {
            errors.Add("AiRuntime:BaseUrl: must be an absolute URI.");
            return;
        }

        if (LoopbackHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            errors.Add("AiRuntime:BaseUrl: must not be a loopback address.");
    }

    private static void ValidateArtifactStorage(IConfiguration configuration, List<string> errors)
    {
        var provider = configuration["ArtifactStorage:Provider"]?.Trim();
        if (string.IsNullOrEmpty(provider))
        {
            errors.Add("ArtifactStorage:Provider: must be set explicitly.");
            return;
        }

        var allowLocal = Flag(
            configuration,
            "ProductionSafety:AllowLocalArtifactStorage",
            false,
            errors);

        if (string.Equals(provider, "Local", StringComparison.OrdinalIgnoreCase))
        {
            if (!allowLocal)
                errors.Add("ArtifactStorage:Provider: local filesystem storage is not permitted (ProductionSafety:AllowLocalArtifactStorage is a deliberate, temporary override).");
            return;
        }

        if (!string.Equals(provider, "S3", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("ArtifactStorage:Provider: must be one of the supported providers: Local or S3.");
            return;
        }

        var endpoint = configuration["ObjectStorage:Endpoint"]?.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme != Uri.UriSchemeHttps ||
            endpointUri.IsLoopback ||
            LoopbackHosts.Contains(endpointUri.Host, StringComparer.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(endpointUri.UserInfo))
            errors.Add("ObjectStorage:Endpoint: must be an explicit non-loopback HTTPS endpoint without embedded credentials.");

        if (!ObjectStorageOptions.IsValidBucketName(configuration["ObjectStorage:Bucket"]))
            errors.Add("ObjectStorage:Bucket: must be a valid private S3 bucket name.");

        if (string.IsNullOrWhiteSpace(configuration["ObjectStorage:Region"]))
            errors.Add("ObjectStorage:Region: is required.");

        if (string.IsNullOrWhiteSpace(configuration["ObjectStorage:AccessKeyId"]))
            errors.Add("ObjectStorage:AccessKeyId: is required.");

        if (string.IsNullOrWhiteSpace(configuration["ObjectStorage:SecretAccessKey"]))
            errors.Add("ObjectStorage:SecretAccessKey: is required.");

        if (!ObjectStorageOptions.TryNormalizePrefix(
                configuration["ObjectStorage:Prefix"],
                out _))
            errors.Add("ObjectStorage:Prefix: is invalid.");
    }

    private static void ValidateCors(IConfiguration configuration, List<string> errors)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").GetChildren()
            .Select(x => x.Value?.Trim()).ToArray();
        if (origins.Length == 0)
        {
            errors.Add("Cors:AllowedOrigins: at least one origin must be configured.");
            return;
        }

        foreach (var origin in origins)
        {
            if (string.IsNullOrEmpty(origin) ||
                origin.Contains('*') ||
                !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                LoopbackHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add("Cors:AllowedOrigins: every origin must be an explicit, non-loopback https origin.");
                return;
            }
        }
    }

    private static void ValidateForwardedHeaders(IConfiguration configuration, List<string> errors)
    {
        if (!ForwardedHeadersSetup.IsEnabled(configuration))
            errors.Add("ForwardedHeaders:Enabled: must be true behind the platform proxy; otherwise every client shares the proxy address and the per-IP rate limit collapses into one bucket.");

        var networks = configuration.GetSection($"{ForwardedHeadersSetup.Section}:TrustedNetworks")
            .GetChildren().Select(x => x.Value).ToArray();
        if (networks.Length == 0)
            errors.Add("ForwardedHeaders:TrustedNetworks: at least one proxy network (CIDR) must be configured.");
        else if (networks.Any(x => !ForwardedHeadersSetup.TryParseNetwork(x, out _)))
            errors.Add("ForwardedHeaders:TrustedNetworks: every entry must be a valid CIDR network narrower than /0.");
    }

    private static void ValidateAllowedHosts(IConfiguration configuration, List<string> errors)
    {
        var hosts = configuration["AllowedHosts"]?.Split(
            ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (hosts.Length == 0 || hosts.Contains("*"))
            errors.Add("AllowedHosts: must list the explicit public API host names (not empty or '*').");
    }

    /// <summary>
    /// Strict boolean parsing. An unrecognised value is reported and then read in the
    /// direction that never relaxes a safety control.
    /// </summary>
    private static bool Flag(
        IConfiguration configuration, string key, bool whenUnset, List<string> errors)
    {
        var value = configuration[key]?.Trim();
        if (string.IsNullOrEmpty(value))
            return whenUnset;

        switch (value.ToLowerInvariant())
        {
            case "true" or "1" or "yes" or "on":
                return true;
            case "false" or "0" or "no" or "off":
                return false;
            default:
                errors.Add($"{key}: is not a recognised boolean value.");
                return key == "Database:AutoMigrate" ||
                       key == "Database:RequireTransportSecurity";
        }
    }
}
