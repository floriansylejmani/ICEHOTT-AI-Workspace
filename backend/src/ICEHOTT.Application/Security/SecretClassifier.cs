using System.Text.Json;

namespace ICEHOTT.Application.Security;

public static class SecretClassifier
{
    public const string RedactionMarker = "[REDACTED]";

    public static string? FindCredential(string value) => throw new NotImplementedException();

    public static bool IsCredentialName(string name) => throw new NotImplementedException();

    public static string Redact(string value) => throw new NotImplementedException();

    public static string RedactJson(string json) => throw new NotImplementedException();
}
