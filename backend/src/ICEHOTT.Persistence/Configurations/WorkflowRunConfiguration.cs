using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class WorkflowRunConfiguration
    : IEntityTypeConfiguration<WorkflowRun>
{
    public void Configure(EntityTypeBuilder<WorkflowRun> builder)
    {
        builder.ToTable("workflow_runs", table =>
        {
            table.HasCheckConstraint(
                "CK_workflow_runs_LeaseGeneration",
                "\"LeaseGeneration\" >= 0");
        });

        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.Id, x.WorkspaceId });
        builder.HasAlternateKey(x => new
        {
            x.Id,
            x.WorkflowVersionId,
            x.WorkspaceId
        });

        builder.Property(x => x.IdempotencyKey)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(24)
            .IsRequired()
            .IsConcurrencyToken();
        builder.Property(x => x.CurrentStepKey).HasMaxLength(120);
        builder.Property(x => x.WaitReason)
            .HasConversion<string>()
            .HasMaxLength(20);
        builder.Property(x => x.ErrorCode).HasMaxLength(80);
        builder.Property(x => x.ErrorMessage).HasMaxLength(500);

        builder.HasIndex(x => new
        {
            x.WorkspaceId,
            x.WorkflowDefinitionId,
            x.IdempotencyKey
        }).IsUnique();

        builder.HasIndex(x => new { x.WorkspaceId, x.CreatedAtUtc });
        builder.HasIndex(x => new { x.Status, x.LeaseExpiresAtUtc });
        builder.HasIndex(x => new { x.Status, x.ResumeAtUtc });

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

        builder.HasOne<WorkflowVersion>().WithMany()
            .HasForeignKey(x => new
            {
                x.WorkflowVersionId,
                x.WorkflowDefinitionId,
                x.WorkspaceId
            })
            .HasPrincipalKey(x => new
            {
                x.Id,
                x.WorkflowDefinitionId,
                x.WorkspaceId
            })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(x => x.RequestedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(x => x.RunAsUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
