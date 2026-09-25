using ICEHOTT.Domain.Tools;

namespace ICEHOTT.Application.Abstractions;

public interface IToolOperationalLog
{
    void HandlerFailed(ToolExecution execution, Exception exception);
}
