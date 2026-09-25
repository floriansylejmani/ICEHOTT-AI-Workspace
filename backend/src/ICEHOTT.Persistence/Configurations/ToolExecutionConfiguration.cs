using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class ToolExecutionConfiguration
    : IEntityTypeConfiguration<ToolExecution>
{
    public void Configure(EntityTypeBuilder<ToolExecution> builder)
    {
        builder.ToTable("tool_executions", table =>
        {
            table.HasCheckConstraint(
                "CK_tool_executions_PolicyVersion", "\"PolicyVersion\" >= 0");
            table.HasCheckConstraint(
                "CK_tool_executions_PolicyMinimumRequesterRole",
                "\"PolicyMinimumRequesterRole\" BETWEEN 1 AND 3");
            table.HasCheckConstraint(
                "CK_tool_executions_PolicyApprover",
                "(\"PolicyRequiresApproval\" AND \"PolicyMinimumApproverRole\" IS NOT NULL AND \"PolicyMinimumApproverRole\" BETWEEN 2 AND 3) OR " +
                "(NOT \"PolicyRequiresApproval\" AND \"PolicyMinimumApproverRole\" IS NULL)");
        });
        builder.HasKey(x => x.Id);

        // Phase 4.5 B: effective policy snapshot at admission.
        builder.Property(x => x.PolicyMinimumRequesterRole).HasConversion<int>();
        builder.Property(x => x.PolicyMinimumApproverRole).HasConversion<int?>();
        builder.Ignore(x => x.PolicySnapshot);
        builder.HasAlternateKey(x => new { x.Id, x.WorkspaceId });

        builder.Property(x => x.ToolName)
            .HasMaxLength(160)
            .IsRequired();
        builder.Property(x => x.ArgumentsJson)
            .HasColumnType("text")
            .IsRequired();
        builder.Property(x => x.ArgumentsHash)
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(x => x.IdempotencyKey)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(x => x.ResultJson)
            .HasColumnType("text");
        builder.Property(x => x.ErrorCode)
            .HasMaxLength(80);
        builder.Property(x => x.ErrorMessage)
            .HasMaxLength(500);

        // Every state transition changes Status, so using it as the
        // optimistic concurrency token turns each UPDATE into a guarded
        // compare-and-swap (WHERE "Status" = <status that was read>).
        // Two racing approvers or executors cannot both win the same
        // transition, so a handler can never run twice for one execution.
        // No DDL change: this only affects generated UPDATE statements.
        builder.Property(x => x.Status)
            .IsConcurrencyToken();

        builder.HasIndex(x => new
        {
            x.WorkspaceId,
            x.ToolName,
            x.IdempotencyKey
        }).IsUnique();

        builder.HasIndex(x => new
        {
            x.WorkspaceId,
            x.RequestedAtUtc
        });

        builder.HasIndex(x => new { x.Status, x.LeaseExpiresAtUtc });

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(x => x.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.RequestedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.ApprovedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
