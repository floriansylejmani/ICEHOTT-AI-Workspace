using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ICEHOTT.Application.Security;

/// <summary>
/// Phase 4.5 packet C credential policy (docs/PHASE-4.5-C-BUDGETS-SECRETS.md C6/C7).
/// Classifies credential-shaped field names and known credential formats. It is
/// deliberately a deny-list of recognisable formats: it cannot prove that
/// arbitrary free text holds no secret, and callers must not claim otherwise.
/// </summary>
public static class SecretClassifier
{
    public const string RedactionMarker = "[REDACTED]";

    // Normalised (ASCII letters only, lowercase) field names that designate a
    // credential on their own, and fragments that make any name one.
    private static readonly HashSet<string> CredentialNames = new(StringComparer.Ordinal)
    {
        "auth", "authorization", "proxyauthorization", "cookie", "setcookie",
        "sessionid", "sid", "pwd", "passwd", "pin", "otp", "mfacode"
    };

    private static readonly string[] CredentialNameFragments =
    [
        "password", "passphrase", "secret", "token", "apikey", "accesskey",
        "privatekey", "signingkey", "encryptionkey", "clientkey", "credential",
        "connectionstring", "bearer"
    ];

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    // Known credential formats. Patterns use the linear-time NonBacktracking
    // engine; input size is bounded by the request body limit.
    private static readonly (string Id, Regex Pattern)[] Detectors =
    [
        ("private-key", Pattern(
            @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----[A-Za-z0-9+/=\s:,.\-]*?(-----END [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----|$)")),
        ("aws-access-key-id", Pattern(@"\b(AKIA|ASIA|ABIA|ACCA)[0-9A-Z]{16}\b")),
        ("github-token", Pattern(@"\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,255}")),
        ("github-fine-grained-token", Pattern(@"\bgithub_pat_[A-Za-z0-9_]{22,255}")),
        ("anthropic-api-key", Pattern(@"\bsk-ant-[A-Za-z0-9_\-]{20,255}")),
        ("openai-api-key", Pattern(@"\bsk-(proj-|svcacct-|admin-)?[A-Za-z0-9_\-]{20,255}")),
        ("stripe-key", Pattern(@"\b(sk|rk)_(live|test)_[A-Za-z0-9]{16,255}")),
        ("slack-token", Pattern(@"\bxox[abposr]-[A-Za-z0-9\-]{10,255}")),
        ("google-api-key", Pattern(@"\bAIza[0-9A-Za-z_\-]{35}")),
        ("jwt", Pattern(@"\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}")),
        ("bearer-token", Pattern(@"(?i)\bbearer\s+[A-Za-z0-9._~+/\-]{16,}=*")),
        ("url-credentials", Pattern(@"(?i)\b[a-z][a-z0-9+.\-]*://[^\s/:@""']+:[^\s/@""']+@")),
        ("azure-account-key", Pattern(@"(?i)\b(AccountKey|SharedAccessKey)\s*=\s*[A-Za-z0-9+/=]{20,}")),
        ("credential-assignment", Pattern(
            @"(?i)\b(password|passwd|pwd|passphrase|secret|client[_\-]?secret|api[_\-]?key|access[_\-]?token|auth[_\-]?token|refresh[_\-]?token)[""']?\s*[:=]\s*[""']?[^\s;,""'<>]{4,}"))
    ];

    /// <summary>Returns the id of the first detector that matches <paramref name="value"/>, else null.</summary>
    public static string? FindCredential(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        foreach (var (id, pattern) in Detectors)
        {
            if (IsMatch(pattern, value))
                return id;
        }

        return null;
    }

    /// <summary>
    /// True when a field name designates a credential, ignoring case,
    /// separators and digits (e.g. <c>password</c>, <c>X-Api-Key</c>, <c>client_secret</c>).
    /// </summary>
    public static bool IsCredentialName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var normalized = Normalize(name);
        return CredentialNames.Contains(normalized) ||
               CredentialNameFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));
    }

    /// <summary>Replaces every recognised credential in free text with <see cref="RedactionMarker"/>.</summary>
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        var result = value;
        foreach (var (_, pattern) in Detectors)
        {
            try
            {
                result = pattern.Replace(result, RedactionMarker);
            }
            catch (RegexMatchTimeoutException)
            {
                // Fail closed: text that cannot be classified in time is withheld.
                return RedactionMarker;
            }
        }

        return result;
    }

    /// <summary>
    /// Redacts a JSON document: values under credential-named properties are
    /// replaced wholesale, and credentials inside any other string or property
    /// name are masked. Structure and non-sensitive values are preserved.
    /// Input that is not valid JSON is redacted as free text.
    /// </summary>
    public static string RedactJson(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Redact(json);
        }

        using (document)
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream))
                WriteRedacted(writer, document.RootElement);

            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    /// <summary>
    /// Finds credential-named properties and credential-shaped property names
    /// or string values anywhere in a JSON value. Findings carry a JSON path and
    /// a detector id, never the value; a property name that is itself
    /// credential-shaped is reported at its parent's path so it is not echoed.
    /// </summary>
    public static IReadOnlyList<SecretFinding> Scan(JsonElement element)
    {
        var findings = new List<SecretFinding>();
        Scan(element, "$", findings);
        return findings;
    }

    private static void Scan(JsonElement element, string path, List<SecretFinding> findings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (FindCredential(property.Name) is { } nameDetector)
                    {
                        findings.Add(new SecretFinding(path, nameDetector));
                        continue;
                    }

                    var childPath = $"{path}.{property.Name}";
                    if (IsCredentialName(property.Name))
                    {
                        findings.Add(new SecretFinding(childPath, "credential-field"));
                        continue;
                    }

                    Scan(property.Value, childPath, findings);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    Scan(item, $"{path}[{index++}]", findings);
                break;

            case JsonValueKind.String:
                if (FindCredential(element.GetString()) is { } detector)
                    findings.Add(new SecretFinding(path, detector));
                break;
        }
    }

    private static void WriteRedacted(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(Redact(property.Name));
                    if (IsCredentialName(property.Name))
                        writer.WriteStringValue(RedactionMarker);
                    else
                        WriteRedacted(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteRedacted(writer, item);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(Redact(element.GetString()));
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsMatch(Regex pattern, string value)
    {
        try
        {
            return pattern.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: unclassifiable input is treated as a credential.
            return true;
        }
    }

    private static string Normalize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (char.IsAsciiLetter(character))
                builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static Regex Pattern(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, MatchTimeout);
}

/// <summary>A credential found in structured input: where (JSON path) and which detector.</summary>
public sealed record SecretFinding(string Path, string Detector);
