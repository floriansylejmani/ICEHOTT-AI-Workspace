using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class WorkflowCheckpointConfiguration
    : IEntityTypeConfiguration<WorkflowCheckpoint>
{
    public void Configure(EntityTypeBuilder<WorkflowCheckpoint> builder)
    {
        builder.ToTable("workflow_checkpoints", table =>
        {
            table.HasCheckConstraint(
                "CK_workflow_checkpoints_MinimumApproverRole",
                "\"MinimumApproverRole\" BETWEEN 1 AND 3");
        });

        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.Id, x.WorkspaceId });

        builder.Property(x => x.MinimumApproverRole).HasConversion<int>();
        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired()
            .IsConcurrencyToken();
        builder.Property(x => x.Reason).HasMaxLength(500);

        builder.HasIndex(x => x.StepRunId).IsUnique();
        builder.HasIndex(x => new
        {
            x.WorkspaceId,
            x.Status,
            x.CreatedAtUtc
        });

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
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<WorkflowStepRun>().WithMany()
            .HasForeignKey(x => new
            {
                x.StepRunId,
                x.WorkflowRunId,
                x.WorkspaceId
            })
            .HasPrincipalKey(x => new
            {
                x.Id,
                x.WorkflowRunId,
                x.WorkspaceId
            })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(x => x.RequestedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(x => x.DecidedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
