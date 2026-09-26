using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class WorkflowAuditEventConfiguration
    : IEntityTypeConfiguration<WorkflowAuditEvent>
{
    public void Configure(EntityTypeBuilder<WorkflowAuditEvent> builder)
    {
        builder.ToTable("workflow_audit_events");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.EventType)
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();
        builder.Property(x => x.DetailJson)
            .HasMaxLength(4000);

        builder.HasIndex(x => new
        {
            x.WorkspaceId,
            x.CreatedAtUtc
        });
        builder.HasIndex(x => new
        {
            x.WorkspaceId,
            x.WorkflowRunId,
            x.CreatedAtUtc
        });

        builder.HasOne<Workspace>().WithMany()
            .HasForeignKey(x => x.WorkspaceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<WorkflowDefinition>().WithMany()
            .HasForeignKey(x => new
            {
                x.WorkflowDefinitionId,
                x.WorkspaceId
            })
            .HasPrincipalKey(x => new
            {
                x.Id,
                x.WorkspaceId
            })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<WorkflowRun>().WithMany()
            .HasForeignKey(x => new
            {
                x.WorkflowRunId,
                x.WorkspaceId
            })
            .HasPrincipalKey(x => new
            {
                x.Id,
                x.WorkspaceId
            })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<WorkflowStepRun>().WithMany()
            .HasForeignKey(x => new
            {
                x.WorkflowStepRunId,
                x.WorkspaceId
            })
            .HasPrincipalKey(x => new
            {
                x.Id,
                x.WorkspaceId
            })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<WorkflowTrigger>().WithMany()
            .HasForeignKey(x => new
            {
                x.WorkflowTriggerId,
                x.WorkspaceId
            })
            .HasPrincipalKey(x => new
            {
                x.Id,
                x.WorkspaceId
            })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(x => x.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
