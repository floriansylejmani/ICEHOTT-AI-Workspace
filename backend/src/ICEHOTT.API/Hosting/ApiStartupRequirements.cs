namespace ICEHOTT.API.Hosting;

/// <summary>
/// Startup requirements that only the process serving the public HTTP API has: token
/// signing material and browser origins. The worker exposes no controllers, issues and
/// validates no tokens, so it must not be forced to hold or know either.
/// Applied in every environment; the hosted-environment rules live in
/// <see cref="ProductionConfigurationValidator"/>.
/// </summary>
public static class ApiStartupRequirements
{
    public static IReadOnlyList<string> Validate(
        IConfiguration configuration,
        ServiceRole role,
        bool isDevelopment)
    {
        if (!role.ServesHttpApi())
            return [];

        var errors = new List<string>();

        var key = configuration.GetSection(JwtSection).Get<Jwt>()?.Key;
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
            errors.Add("Jwt:Key must be configured with at least 32 characters.");

        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        if (!isDevelopment && origins.Length == 0)
            errors.Add("Cors:AllowedOrigins must be configured outside Development.");

        return errors;
    }

    private const string JwtSection = "Jwt";

    private sealed class Jwt
    {
        public string? Key { get; init; }
    }
}
