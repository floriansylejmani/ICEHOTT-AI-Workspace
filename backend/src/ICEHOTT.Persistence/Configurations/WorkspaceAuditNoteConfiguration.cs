using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class WorkspaceAuditNoteConfiguration
    : IEntityTypeConfiguration<WorkspaceAuditNote>
{
    public void Configure(
        EntityTypeBuilder<WorkspaceAuditNote> builder)
    {
        builder.ToTable("workspace_audit_notes");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Message)
            .HasMaxLength(500)
            .IsRequired();

        builder.HasIndex(x => x.ToolExecutionId)
            .IsUnique();

        builder.HasOne<ToolExecution>()
            .WithMany()
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
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
