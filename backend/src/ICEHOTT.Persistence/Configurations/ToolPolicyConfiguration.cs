using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class ToolPolicyConfiguration : IEntityTypeConfiguration<ToolPolicy>
{
    public void Configure(EntityTypeBuilder<ToolPolicy> builder)
    {
        builder.ToTable("tool_policies", table =>
        {
            table.HasCheckConstraint("CK_tool_policies_Version", "\"Version\" >= 0");
            table.HasCheckConstraint(
                "CK_tool_policies_MinimumRequesterRole",
                "\"MinimumRequesterRole\" IS NULL OR \"MinimumRequesterRole\" BETWEEN 1 AND 3");
            table.HasCheckConstraint(
                "CK_tool_policies_MinimumApproverRole",
                "\"MinimumApproverRole\" IS NULL OR \"MinimumApproverRole\" BETWEEN 2 AND 3");
            table.HasCheckConstraint(
                "CK_tool_policies_MaxArgumentLength",
                "\"MaxArgumentLength\" IS NULL OR \"MaxArgumentLength\" > 0");
        });

        // One overlay per tool per workspace; the key itself is the tenant scope.
        builder.HasKey(x => new { x.WorkspaceId, x.ToolName });

        builder.Property(x => x.ToolName).HasMaxLength(160).IsRequired();

        // Optimistic concurrency: Owner updates and approval guards compare
        // the version that was read (docs/PHASE-4.5-B-TOOL-POLICY.md D3/D6).
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.Property(x => x.MinimumRequesterRole).HasConversion<int?>();
        builder.Property(x => x.MinimumApproverRole).HasConversion<int?>();

        builder.Ignore(x => x.Settings);

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(x => x.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UpdatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ToolPolicyAuditEventConfiguration : IEntityTypeConfiguration<ToolPolicyAuditEvent>
{
    public void Configure(EntityTypeBuilder<ToolPolicyAuditEvent> builder)
    {
        builder.ToTable("tool_policy_audit_events", table =>
            table.HasCheckConstraint(
                "CK_tool_policy_audit_events_Versions",
                "\"NewVersion\" = \"PreviousVersion\" + 1"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.ToolName).HasMaxLength(160).IsRequired();
        builder.Property(x => x.PreviousPolicyJson).HasColumnType("text");
        builder.Property(x => x.NewPolicyJson).HasColumnType("text").IsRequired();

        // At most one audit event per resulting version (backstop against a
        // double write for the same transition).
        builder.HasIndex(x => new { x.WorkspaceId, x.ToolName, x.NewVersion }).IsUnique();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(x => x.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
