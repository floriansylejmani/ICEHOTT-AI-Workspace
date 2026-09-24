using ICEHOTT.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class KnowledgeChunkConfiguration : IEntityTypeConfiguration<KnowledgeChunk>
{
    public void Configure(EntityTypeBuilder<KnowledgeChunk> builder)
    {
        builder.ToTable("knowledge_chunks");
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.Id, x.WorkspaceId });
        builder.Property(x => x.Content).HasColumnType("text").IsRequired();
        builder.HasIndex(x => new { x.WorkspaceId, x.DocumentId, x.Ordinal });

        builder.HasOne<KnowledgeDocument>().WithMany()
            .HasForeignKey(x => new { x.DocumentId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
