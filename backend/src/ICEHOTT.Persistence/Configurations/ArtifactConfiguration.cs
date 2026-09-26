using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class ArtifactConfiguration
    : IEntityTypeConfiguration<Artifact>
{
    public void Configure(EntityTypeBuilder<Artifact> builder)
    {
        builder.ToTable("artifacts", table =>
        {
            table.HasCheckConstraint(
                "CK_artifacts_SizeBytes",
                "\"SizeBytes\" >= 0");
        });

        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.Id, x.WorkspaceId });

        builder.Property(x => x.FileName)
            .HasMaxLength(260)
            .IsRequired();
        builder.Property(x => x.ContentType)
            .HasMaxLength(160)
            .IsRequired();
        builder.Property(x => x.Sha256)
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(x => x.StorageKey)
            .HasMaxLength(500)
            .IsRequired();
        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.HasIndex(x => x.StorageKey).IsUnique();
        builder.HasIndex(x => new { x.WorkspaceId, x.CreatedAtUtc });

        builder.HasOne<Workspace>().WithMany()
            .HasForeignKey(x => x.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(x => x.CreatedByUserId)
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
            .OnDelete(DeleteBehavior.Restrict);
    }
}
