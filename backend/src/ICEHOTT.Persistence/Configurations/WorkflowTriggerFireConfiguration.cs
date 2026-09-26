using ICEHOTT.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class WorkflowTriggerFireConfiguration
    : IEntityTypeConfiguration<WorkflowTriggerFire>
{
    public void Configure(EntityTypeBuilder<WorkflowTriggerFire> builder)
    {
        builder.ToTable("workflow_trigger_fires");
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.Id, x.WorkspaceId });

        builder.Property(x => x.FireKey)
            .HasMaxLength(160)
            .IsRequired();
        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired()
            .IsConcurrencyToken();

        builder.HasIndex(x => new
        {
            x.TriggerId,
            x.ScheduledForUtc
        }).IsUnique();
        builder.HasIndex(x => x.FireKey).IsUnique();
        builder.HasIndex(x => new
        {
            x.WorkspaceId,
            x.Status,
            x.ScheduledForUtc
        });

        builder.HasOne<WorkflowTrigger>().WithMany()
            .HasForeignKey(x => new
            {
                x.TriggerId,
                x.WorkflowVersionId,
                x.WorkspaceId
            })
            .HasPrincipalKey(x => new
            {
                x.Id,
                x.WorkflowVersionId,
                x.WorkspaceId
            })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<WorkflowRun>().WithMany()
            .HasForeignKey(x => new
            {
                x.WorkflowRunId,
                x.WorkflowVersionId,
                x.WorkspaceId
            })
            .HasPrincipalKey(x => new
            {
                x.Id,
                x.WorkflowVersionId,
                x.WorkspaceId
            })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
