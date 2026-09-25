using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class ToolQuotaCounterConfiguration : IEntityTypeConfiguration<ToolQuotaCounter>
{
    public void Configure(EntityTypeBuilder<ToolQuotaCounter> builder)
    {
        builder.ToTable("tool_quota_counters", table =>
        {
            table.HasCheckConstraint("CK_tool_quota_counters_Count", "\"Count\" >= 0");
            table.HasCheckConstraint(
                "CK_tool_quota_counters_WindowStartUnixSeconds",
                "\"WindowStartUnixSeconds\" >= 0");
        });

        // One counter per (workspace, tool). The row is only written by the
        // atomic admission upsert (docs/PHASE-4.5-C-BUDGETS-SECRETS.md C2).
        builder.HasKey(x => new { x.WorkspaceId, x.ToolName });
        builder.Property(x => x.ToolName).HasMaxLength(160).IsRequired();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(x => x.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
