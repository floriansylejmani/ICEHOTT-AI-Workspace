using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class WorkflowStepRunConfiguration
    : IEntityTypeConfiguration<WorkflowStepRun>
{
    public void Configure(EntityTypeBuilder<WorkflowStepRun> builder)
    {
        builder.ToTable("workflow_step_runs", table =>
        {
            table.HasCheckConstraint(
                "CK_workflow_step_runs_Attempt",
                "\"Attempt\" >= 1");
        });

        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.Id, x.WorkspaceId });
        builder.HasAlternateKey(x => new
        {
            x.Id,
            x.WorkflowRunId,
            x.WorkspaceId
        });

        builder.Property(x => x.StepKey)
            .HasMaxLength(120)
            .IsRequired();
        builder.Property(x => x.StepType)
            .HasConversion<string>()
            .HasMaxLength(24)
            .IsRequired();
        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired()
            .IsConcurrencyToken();
        builder.Property(x => x.InputJson)
            .HasColumnType("text")
            .IsRequired();
        builder.Property(x => x.OutputJson)
            .HasColumnType("text");
        builder.Property(x => x.ErrorCode).HasMaxLength(80);
        builder.Property(x => x.ErrorMessage).HasMaxLength(500);

        builder.HasIndex(x => new
        {
            x.WorkflowRunId,
            x.StepKey,
            x.Attempt
        }).IsUnique();

        builder.HasIndex(x => new
        {
            x.WorkspaceId,
            x.Status,
            x.NextAttemptAtUtc
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

        builder.HasOne<ToolExecution>().WithMany()
            .HasForeignKey(x => new
            {
                x.ToolExecutionId,
                x.WorkspaceId
            })
            .HasPrincipalKey(x => new
            {
                x.Id,
                x.WorkspaceId
            })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
