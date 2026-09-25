namespace ICEHOTT.Domain.Tools;

public sealed class ToolQuotaCounter
{
    private ToolQuotaCounter() { }

    public Guid WorkspaceId { get; private set; }
    public string ToolName { get; private set; } = string.Empty;
    public long WindowStartUnixSeconds { get; private set; }
    public int Count { get; private set; }
}
