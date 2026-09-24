namespace ICEHOTT.Domain.Workspaces;

public sealed class Workspace
{
    private Workspace() { }

    public Workspace(Guid id, string name, string slug, Guid createdByUserId, DateTimeOffset createdAtUtc)
    {
        Id = id;
        Name = name.Trim();
        Slug = slug;
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
}
