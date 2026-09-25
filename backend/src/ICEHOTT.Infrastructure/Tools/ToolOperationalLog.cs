using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Tools;
using Microsoft.Extensions.Logging;

namespace ICEHOTT.Infrastructure.Tools;

public sealed class ToolOperationalLog(ILogger<ToolOperationalLog> logger) : IToolOperationalLog
{
    public void HandlerFailed(ToolExecution execution, Exception exception)
    {
    }
}
