using System.Globalization;
using System.Text.RegularExpressions;

namespace ICEHOTT.API.Hosting;

/// <summary>
/// Strongly typed <c>Observability</c> configuration. Validation reads the raw configuration
/// strings so that a malformed value can be reported by key without ever echoing the value
/// (endpoint credentials and OTLP headers are secrets).
/// </summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";
    public const string ProtocolGrpc = "grpc";
    public const string ProtocolHttp = "http/protobuf";

    private static readonly Regex ServiceNamePattern =
        new("^[a-z0-9][a-z0-9._-]{0,62}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HeaderNamePattern =
        new("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool Enabled { get; init; }
    public bool Required { get; init; }
    public string? ServiceName { get; init; }
    public string Endpoint { get; init; } = string.Empty;
    public string Protocol { get; init; } = ProtocolGrpc;
    public bool AllowInsecureTransport { get; init; }
    public string? Headers { get; init; }
    public int TimeoutSeconds { get; init; } = 5;
    public bool TracesEnabled { get; init; } = true;
    public double SamplingRatio { get; init; } = 0.1;
    public bool MetricsEnabled { get; init; } = true;
    public bool LogsEnabled { get; init; } = true;

    /// <summary>
    /// Validates telemetry configuration. Telemetry is auxiliary: nothing is required unless it is
    /// enabled (or explicitly marked Required). Messages name keys only, never values.
    /// </summary>
    public static IReadOnlyList<string> Validate(IConfiguration configuration, bool hosted)
    {
        var errors = new List<string>();
        var enabled = ReadBool(configuration, "Observability:Enabled", false, errors);
        var required = ReadBool(configuration, "Observability:Required", false, errors);

        if (required && !enabled)
            errors.Add("Observability:Enabled: must be true while Observability:Required is true.");

        // Malformed flags are reported even when disabled, so a typo cannot silently disable telemetry.
        var allowInsecure = ReadBool(configuration, "Observability:Otlp:AllowInsecureTransport", false, errors);
        var traces = ReadBool(configuration, "Observability:Traces:Enabled", true, errors);
        var metrics = ReadBool(configuration, "Observability:Metrics:Enabled", true, errors);
        var logs = ReadBool(configuration, "Observability:Logs:Enabled", true, errors);

        if (!enabled)
            return errors;

        var name = configuration["Observability:ServiceName"]?.Trim();
        if (!string.IsNullOrEmpty(name) && !ServiceNamePattern.IsMatch(name))
            errors.Add("Observability:ServiceName: must be 1-63 lowercase letters, digits, '.', '_' or '-'.");

        var endpoint = configuration["Observability:Otlp:Endpoint"]?.Trim();
        if (string.IsNullOrEmpty(endpoint))
        {
            errors.Add("Observability:Otlp:Endpoint: is required while observability is enabled.");
        }
        else if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                 uri.Scheme is not ("http" or "https"))
        {
            errors.Add("Observability:Otlp:Endpoint: must be an absolute http or https URL.");
        }
        else
        {
            if (!string.IsNullOrEmpty(uri.UserInfo))
                errors.Add("Observability:Otlp:Endpoint: must not embed credentials; use Observability:Otlp:Headers.");
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                errors.Add("Observability:Otlp:Endpoint: must not contain a query string or fragment.");
            if (hosted && uri.Scheme != Uri.UriSchemeHttps && !allowInsecure)
                errors.Add("Observability:Otlp:Endpoint: must use https unless Observability:Otlp:AllowInsecureTransport is explicitly true.");
        }

        var protocol = configuration["Observability:Otlp:Protocol"]?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(protocol) && protocol is not (ProtocolGrpc or ProtocolHttp))
            errors.Add("Observability:Otlp:Protocol: must be 'grpc' or 'http/protobuf'.");

        var ratio = configuration["Observability:Traces:SamplingRatio"]?.Trim();
        if (!string.IsNullOrEmpty(ratio) &&
            (!double.TryParse(ratio, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
             double.IsNaN(parsed) || parsed < 0d || parsed > 1d))
            errors.Add("Observability:Traces:SamplingRatio: must be a number between 0 and 1.");

        var timeout = configuration["Observability:Otlp:TimeoutSeconds"]?.Trim();
        if (!string.IsNullOrEmpty(timeout) &&
            (!int.TryParse(timeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ||
             seconds is < 1 or > 30))
            errors.Add("Observability:Otlp:TimeoutSeconds: must be an integer between 1 and 30.");

        var headers = configuration["Observability:Otlp:Headers"];
        if (!string.IsNullOrWhiteSpace(headers) && !HeadersAreWellFormed(headers))
            errors.Add("Observability:Otlp:Headers: must be a comma-separated list of name=value pairs.");

        if (!traces && !metrics && !logs)
            errors.Add("Observability:Enabled: at least one of Traces, Metrics or Logs must be enabled.");

        return errors;
    }

    public static ObservabilityOptions Load(IConfiguration configuration)
    {
        // Call Validate first; values here are known to be well-formed.
        var section = configuration.GetSection(SectionName);
        var protocol = section["Otlp:Protocol"]?.Trim().ToLowerInvariant();
        return new ObservabilityOptions
        {
            Enabled = Flag(section["Enabled"], false),
            Required = Flag(section["Required"], false),
            ServiceName = section["ServiceName"]?.Trim(),
            Endpoint = section["Otlp:Endpoint"]?.Trim() ?? string.Empty,
            Protocol = string.IsNullOrEmpty(protocol) ? ProtocolGrpc : protocol,
            AllowInsecureTransport = Flag(section["Otlp:AllowInsecureTransport"], false),
            Headers = section["Otlp:Headers"],
            TimeoutSeconds = int.TryParse(
                section["Otlp:TimeoutSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 5,
            TracesEnabled = Flag(section["Traces:Enabled"], true),
            SamplingRatio = double.TryParse(
                section["Traces:SamplingRatio"], NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0.1,
            MetricsEnabled = Flag(section["Metrics:Enabled"], true),
            LogsEnabled = Flag(section["Logs:Enabled"], true)
        };
    }

    /// <summary>Signal endpoint for the chosen protocol (HTTP requires the per-signal path).</summary>
    public Uri SignalEndpoint(string signalPath)
    {
        var baseUri = new Uri(Endpoint.TrimEnd('/') + "/");
        return Protocol == ProtocolHttp ? new Uri(baseUri, signalPath) : new Uri(Endpoint);
    }

    private static bool Flag(string? value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static bool ReadBool(IConfiguration configuration, string key, bool fallback, List<string> errors)
    {
        var raw = configuration[key]?.Trim();
        if (string.IsNullOrEmpty(raw))
            return fallback;
        if (bool.TryParse(raw, out var value))
            return value;
        errors.Add($"{key}: must be 'true' or 'false'.");
        return fallback;
    }

    private static bool HeadersAreWellFormed(string headers) =>
        headers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .All(pair =>
            {
                var index = pair.IndexOf('=');
                return index > 0 && index < pair.Length - 1 &&
                       HeaderNamePattern.IsMatch(pair[..index].Trim());
            });
}
