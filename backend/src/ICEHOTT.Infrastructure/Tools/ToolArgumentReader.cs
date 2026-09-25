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
            var text = value.GetString()?.Trim() ?? string.Empty;
            if (text.Length == 0)
                errors.Add($"Argument '{propertyName}' is required.");
            else if (text.Length > maxLength)
                errors.Add(
                    $"Argument '{propertyName}' must be at most {maxLength} characters.");
        }

        return errors.Count == 0
            ? ToolArgumentValidationResult.Valid
            : new ToolArgumentValidationResult(false, errors);
    }

    public static string RequiredString(
        JsonElement arguments,
        string propertyName) =>
        arguments.GetProperty(propertyName).GetString()!.Trim();
}
