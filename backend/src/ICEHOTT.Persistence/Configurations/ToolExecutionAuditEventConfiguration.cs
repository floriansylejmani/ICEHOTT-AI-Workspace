using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class ToolExecutionAuditEventConfiguration
    : IEntityTypeConfiguration<ToolExecutionAuditEvent>
{
    public void Configure(
        EntityTypeBuilder<ToolExecutionAuditEvent> builder)
    {
        builder.ToTable("tool_execution_audit_events");
        builder.HasKey(x => x.Id);

        builder.HasIndex(x => new
        {
            x.WorkspaceId,
            x.ExecutionId,
            x.OccurredAtUtc
        });

        builder.HasOne<ToolExecution>()
            .WithMany()
            .HasForeignKey(x => new
            {
                x.ExecutionId,
                x.WorkspaceId
            })
            .HasPrincipalKey(x => new
            {
                x.Id,
                x.WorkspaceId
            })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
