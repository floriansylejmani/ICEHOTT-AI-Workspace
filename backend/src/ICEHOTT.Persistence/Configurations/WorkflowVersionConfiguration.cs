using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class WorkflowVersionConfiguration
    : IEntityTypeConfiguration<WorkflowVersion>
{
    public void Configure(EntityTypeBuilder<WorkflowVersion> builder)
    {
        builder.ToTable("workflow_versions", table =>
        {
            table.HasCheckConstraint(
                "CK_workflow_versions_VersionNumber",
                "\"VersionNumber\" >= 1");
        });

        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.Id, x.WorkspaceId });
        builder.HasAlternateKey(x => new
        {
            x.Id,
            x.WorkflowDefinitionId,
            x.WorkspaceId
        });

        builder.Property(x => x.DefinitionJson)
            .HasColumnType("text")
            .IsRequired();
        builder.Property(x => x.DefinitionHash)
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.HasIndex(x => new
        {
            x.WorkflowDefinitionId,
            x.VersionNumber
        }).IsUnique();

        builder.HasIndex(
                x => x.WorkflowDefinitionId,
                "IX_workflow_versions_Active")
            .IsUnique()
            .HasFilter("\"Status\" = 'Active'");

        builder.HasIndex(x => new
        {
            x.WorkspaceId,
            x.WorkflowDefinitionId,
            x.CreatedAtUtc
        });

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
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(x => x.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
