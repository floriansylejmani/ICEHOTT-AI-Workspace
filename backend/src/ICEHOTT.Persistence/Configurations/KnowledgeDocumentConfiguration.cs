using ICEHOTT.Domain.Knowledge;
using ICEHOTT.Domain.Users;
using ICEHOTT.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class KnowledgeDocumentConfiguration : IEntityTypeConfiguration<KnowledgeDocument>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocument> builder)
    {
        builder.ToTable("knowledge_documents");
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.Id, x.WorkspaceId });
        builder.Property(x => x.Title).HasMaxLength(200).IsRequired();
        builder.Property(x => x.SourceName).HasMaxLength(260);
        builder.Property(x => x.Content).HasColumnType("text").IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.HasIndex(x => new { x.WorkspaceId, x.CreatedAtUtc });

        builder.HasOne<Workspace>().WithMany()
            .HasForeignKey(x => x.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(x => x.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
