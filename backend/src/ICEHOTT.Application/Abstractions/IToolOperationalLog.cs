using ICEHOTT.Domain.Tools;

namespace ICEHOTT.Application.Abstractions;

/// <summary>
/// Secured operational diagnostics for tool execution (docs/PHASE-4.5-C-BUDGETS-SECRETS.md C8).
/// Implementations must never write tool arguments or results, and must redact
/// exception text before it reaches a log sink.
/// </summary>
public interface IToolOperationalLog
{
    void HandlerFailed(ToolExecution execution, Exception exception);
}
