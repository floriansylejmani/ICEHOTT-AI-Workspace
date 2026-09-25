using System.Text.Json;
using ICEHOTT.Application.Tools;

namespace ICEHOTT.Infrastructure.Tools;

internal static class ToolArgumentReader
{
    public static ToolArgumentValidationResult ValidateSingleRequiredString(
        JsonElement arguments,
        string propertyName,
        int maxLength)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return ToolArgumentValidationResult.Invalid(
                "Tool arguments must be a JSON object.");

        var errors = new List<string>();

        foreach (var property in arguments.EnumerateObject())
        {
            if (!string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.Ordinal))
                errors.Add($"Unknown argument '{property.Name}'.");
        }

        if (!arguments.TryGetProperty(propertyName, out var value))
            errors.Add($"Argument '{propertyName}' is required.");
        else if (value.ValueKind != JsonValueKind.String)
            errors.Add($"Argument '{propertyName}' must be a string.");
        else
        {
            // The length bound applies to the raw value, not the trimmed
            // one: the raw value is what gets persisted in ArgumentsJson,
            // so measuring after Trim() let whitespace padding bypass it.
            var raw = value.GetString() ?? string.Empty;
            if (raw.Trim().Length == 0)
                errors.Add($"Argument '{propertyName}' is required.");
            else if (raw.Length > maxLength)
                errors.Add(
                    $"Argument '{propertyName}' must be at most {maxLength} characters.");
            else if (raw.Any(IsDisallowedControlCharacter))
                errors.Add(
                    $"Argument '{propertyName}' contains unsupported control characters.");
        }

        return errors.Count == 0
            ? ToolArgumentValidationResult.Valid
            : new ToolArgumentValidationResult(false, errors);
    }

    // NUL is rejected by PostgreSQL text columns (SQLSTATE 22021) and other
    // C0/C1 controls have no business in tool text. Tab/CR/LF stay allowed.
    private static bool IsDisallowedControlCharacter(char character) =>
        char.IsControl(character) &&
        character is not ('\t' or '\n' or '\r');

    public static string RequiredString(
        JsonElement arguments,
        string propertyName) =>
        arguments.GetProperty(propertyName).GetString()!.Trim();
}
