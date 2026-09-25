using Microsoft.AspNetCore.Mvc.Filters;

namespace ICEHOTT.API.Filters;

public static class ToolRequestLimits
{
    public const int MaxExecutionRequestBytes = 32 * 1024;
    public const int MaxPolicyRequestBytes = 4 * 1024;
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequestBodyLimitAttribute(int maxBytes) : Attribute, IAsyncResourceFilter
{
    public int MaxBytes { get; } = maxBytes;

    public Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next) => next();
}
