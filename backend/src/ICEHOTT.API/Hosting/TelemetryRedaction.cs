using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace ICEHOTT.API.Hosting;

/// <summary>Central redaction policy shared by span and log processors.</summary>
public static class TelemetryRedaction
{
    public const string Redacted = "[redacted]";

    /// <summary>Tags removed outright: they can carry SQL text, query strings or payloads.</summary>
    private static readonly HashSet<string> DroppedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "db.statement", "db.query.text", "db.user", "db.connection_string", "url.query", "url.full",
        "url.original", "http.url", "http.target", "http.request.body", "http.response.body",
        "exception.stacktrace", "exception.message"
    };

    private static readonly Regex SensitiveName = new(
        "(password|passwd|secret|token|authorization|cookie|api[_-]?key|credential|connection[_-]?string|prompt|content|payload|bearer|jwt|private[_-]?key)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsDropped(string name) =>
        DroppedTags.Contains(name) ||
        name.StartsWith("db.query.parameter", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("http.request.header.", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("http.response.header.", StringComparison.OrdinalIgnoreCase);

    public static bool IsSensitiveName(string name) => SensitiveName.IsMatch(name);
}

/// <summary>Removes or masks sensitive tags on every span before export.</summary>
public sealed class SensitiveTagRedactionProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        List<string>? drop = null;
        List<string>? mask = null;
        foreach (var tag in data.TagObjects)
        {
            if (TelemetryRedaction.IsDropped(tag.Key))
                (drop ??= []).Add(tag.Key);
            else if (TelemetryRedaction.IsSensitiveName(tag.Key))
                (mask ??= []).Add(tag.Key);
        }

        if (drop is not null)
            foreach (var key in drop)
                data.SetTag(key, null);
        if (mask is not null)
            foreach (var key in mask)
                data.SetTag(key, TelemetryRedaction.Redacted);
    }
}

/// <summary>
/// Strips exception messages/stack traces and masks sensitive attribute names on log records
/// before export. Console logging is unaffected.
/// </summary>
public sealed class SensitiveLogRedactionProcessor : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord data)
    {
        if (data.Exception is { } exception)
        {
            var withType = (data.Attributes ?? []).ToList();
            withType.Add(new("exception.type", exception.GetType().FullName));
            data.Attributes = withType;
            data.Exception = null;
        }

        if (data.Attributes is { Count: > 0 } current &&
            current.Any(a => TelemetryRedaction.IsSensitiveName(a.Key) || TelemetryRedaction.IsDropped(a.Key)))
        {
            data.Attributes = current
                .Where(a => !TelemetryRedaction.IsDropped(a.Key))
                .Select(a => TelemetryRedaction.IsSensitiveName(a.Key)
                    ? new KeyValuePair<string, object?>(a.Key, TelemetryRedaction.Redacted)
                    : a)
                .ToList();
        }
    }
}
