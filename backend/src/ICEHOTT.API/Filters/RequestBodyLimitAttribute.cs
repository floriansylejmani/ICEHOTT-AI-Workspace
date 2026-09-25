using System.Buffers;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ICEHOTT.API.Filters;

/// <summary>Request body bounds for tool endpoints (docs/PHASE-4.5-C-BUDGETS-SECRETS.md C5).</summary>
public static class ToolRequestLimits
{
    /// <summary>
    /// Tool execution requests (and the body-less approve/reject routes). Twice
    /// <c>ToolExecutionService.MaxArgumentsBytes</c> leaves room for JSON escaping,
    /// the tool name and the idempotency key around the largest admissible arguments.
    /// </summary>
    public const int MaxExecutionRequestBytes = 32 * 1024;

    /// <summary>Tool policy updates: a handful of scalar fields.</summary>
    public const int MaxPolicyRequestBytes = 4 * 1024;
}

/// <summary>
/// Rejects a request body larger than <see cref="MaxBytes"/> with
/// <c>413 request_body_too_large</c> before model binding, i.e. before any JSON
/// parsing. A declared Content-Length is checked up front; a body without one
/// (chunked) is read into a bounded buffer and rejected as soon as it exceeds
/// the limit. The server's own per-request limit is lowered to match where the
/// host allows it. Runs after authentication and authorization.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequestBodyLimitAttribute(int maxBytes) : Attribute, IAsyncResourceFilter
{
    public int MaxBytes { get; } = maxBytes > 0
        ? maxBytes
        : throw new ArgumentOutOfRangeException(nameof(maxBytes));

    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var request = http.Request;

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } serverLimit)
            serverLimit.MaxRequestBodySize = MaxBytes;

        if (request.ContentLength > MaxBytes)
        {
            context.Result = TooLarge();
            return;
        }

        if (request.ContentLength == 0 ||
            http.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false })
        {
            await next();
            return;
        }

        var buffered = await ReadBoundedAsync(request.Body, http.RequestAborted);
        if (buffered is null)
        {
            context.Result = TooLarge();
            return;
        }

        request.Body = buffered;
        request.ContentLength = buffered.Length;
        await next();
    }

    /// <summary>Reads at most MaxBytes; null when the body is larger.</summary>
    private async Task<MemoryStream?> ReadBoundedAsync(Stream body, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                    return null;
                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        buffer.Position = 0;
        return buffer;
    }

    private ObjectResult TooLarge() =>
        new(new
        {
            code = "request_body_too_large",
            errors = new[] { $"Request body must be at most {MaxBytes} bytes." }
        })
        {
            StatusCode = StatusCodes.Status413PayloadTooLarge
        };
}
