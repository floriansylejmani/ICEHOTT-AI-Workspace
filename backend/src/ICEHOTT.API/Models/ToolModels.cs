using System.Text.Json;

namespace ICEHOTT.API.Models;

public sealed record CreateToolExecutionRequest(
    string? ToolName,
    JsonElement Arguments,
    string? IdempotencyKey);
