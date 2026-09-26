using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class WorkflowTriggerConfiguration
    : IEntityTypeConfiguration<WorkflowTrigger>
{
    public void Configure(EntityTypeBuilder<WorkflowTrigger> builder)
    {
        builder.ToTable("workflow_triggers");
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.Id, x.WorkspaceId });
        builder.HasAlternateKey(x => new
        {
            x.Id,
            x.WorkflowVersionId,
            x.WorkspaceId
        });

        builder.Property(x => x.Type)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();
        builder.Property(x => x.ScheduleExpression)
            .HasMaxLength(200)
            .IsRequired();
        builder.Property(x => x.TimeZoneId)
            .HasMaxLength(120)
            .IsRequired();

        builder.HasIndex(x => new
        {
            x.WorkspaceId,
            x.Enabled,
            x.NextRunAtUtc
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
            .HasForeignKey(x => x.RunAsUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(x => x.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
