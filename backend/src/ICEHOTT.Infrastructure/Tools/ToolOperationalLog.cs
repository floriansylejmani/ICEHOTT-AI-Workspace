using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Security;
using ICEHOTT.Domain.Tools;
using Microsoft.Extensions.Logging;

namespace ICEHOTT.Infrastructure.Tools;

/// <summary>
/// Logs handler failures with identifiers, the exception type chain, redacted
/// messages and the stack trace. The exception object itself is never handed
/// to the logger: sinks would render its raw message, inner exceptions and
/// Data, any of which can carry credentials. Tool arguments and results are
/// never logged.
/// </summary>
public sealed class ToolOperationalLog(ILogger<ToolOperationalLog> logger) : IToolOperationalLog
{
    private const int MaxInnerExceptions = 5;

    public void HandlerFailed(ToolExecution execution, Exception exception)
    {
        var chain = new List<string>();
        for (var current = exception.InnerException;
             current is not null && chain.Count < MaxInnerExceptions;
             current = current.InnerException)
            chain.Add($"{current.GetType().FullName}: {SecretClassifier.Redact(current.Message)}");

        logger.LogError(
            "Tool handler failed. Tool {ToolName}, execution {ExecutionId}, workspace {WorkspaceId}: " +
            "{ExceptionType}: {ExceptionMessage}. Inner: {InnerExceptions}. Stack: {StackTrace}",
            execution.ToolName,
            execution.Id,
            execution.WorkspaceId,
            exception.GetType().FullName,
            SecretClassifier.Redact(exception.Message),
            chain.Count == 0 ? "none" : string.Join(" <- ", chain),
            exception.StackTrace);
    }
}
