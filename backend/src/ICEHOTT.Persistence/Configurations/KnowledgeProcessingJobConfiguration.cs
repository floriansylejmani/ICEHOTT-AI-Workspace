using ICEHOTT.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class KnowledgeProcessingJobConfiguration : IEntityTypeConfiguration<KnowledgeProcessingJob>
{
    public void Configure(EntityTypeBuilder<KnowledgeProcessingJob> builder)
    {
        builder.ToTable("knowledge_processing_jobs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();
        builder.Property(x => x.LockedBy).HasMaxLength(200);
        builder.Property(x => x.LastError).HasMaxLength(2000);
        builder.HasIndex(x => new { x.Status, x.AvailableAtUtc });
        builder.HasIndex(x => new { x.Status, x.LockedUntilUtc });
        builder.HasIndex(x => new { x.DocumentId, x.WorkspaceId }).IsUnique();
        builder.HasIndex(x => new { x.WorkspaceId, x.CreatedAtUtc });

        builder.HasOne<KnowledgeDocument>().WithMany()
            .HasForeignKey(x => new { x.DocumentId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
