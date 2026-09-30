using System.Net;
using System.Text.RegularExpressions;

namespace ICEHOTT.Infrastructure.Artifacts;

public sealed class ObjectStorageOptions
{
    public const string SectionName = "ObjectStorage";

    private static readonly Regex BucketPattern = new(
        "^[a-z0-9](?:[a-z0-9.-]{1,61}[a-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Endpoint { get; init; } = string.Empty;
    public string Bucket { get; init; } = string.Empty;
    public string Region { get; init; } = "us-east-1";
    public string AccessKeyId { get; init; } = string.Empty;
    public string SecretAccessKey { get; init; } = string.Empty;
    public bool ForcePathStyle { get; init; } = true;
    public string Prefix { get; init; } = string.Empty;

    public static bool TryNormalizePrefix(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var candidate = value.Trim();
        if (candidate.Length == 0)
            return true;

        if (candidate.Length > 256 ||
            candidate.StartsWith('/') ||
            candidate.EndsWith('/') ||
            candidate.Contains('\\') ||
            candidate.Contains("//", StringComparison.Ordinal) ||
            candidate.Any(char.IsControl))
            return false;

        var segments = candidate.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 ||
            segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) ||
                      character is '-' or '_' or '.'))))
            return false;

        normalized = string.Join('/', segments);
        return true;
    }

    public static bool IsValidBucketName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var bucket = value.Trim();
        if (bucket.Length is < 3 or > 63 ||
            !BucketPattern.IsMatch(bucket) ||
            bucket.Contains("..", StringComparison.Ordinal) ||
            bucket.Contains(".-", StringComparison.Ordinal) ||
            bucket.Contains("-.", StringComparison.Ordinal) ||
            IPAddress.TryParse(bucket, out _))
            return false;

        return true;
    }
}
