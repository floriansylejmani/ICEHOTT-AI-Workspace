using ICEHOTT.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class RagEvaluationEvidenceConfiguration
    : IEntityTypeConfiguration<RagEvaluationEvidence>
{
    public void Configure(EntityTypeBuilder<RagEvaluationEvidence> builder)
    {
        builder.ToTable("rag_evaluation_evidence");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.DatasetVersion)
            .HasMaxLength(80)
            .IsRequired();
        builder.Property(x => x.Provider)
            .HasMaxLength(120)
            .IsRequired();
        builder.Property(x => x.Model)
            .HasMaxLength(200)
            .IsRequired();
        builder.Property(x => x.RunnerVersion)
            .HasMaxLength(80)
            .IsRequired();
        builder.Property(x => x.EvaluationKind)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.HasIndex(x => new
        {
            x.EmbeddingProfileId,
            x.DatasetVersion,
            x.EvaluationKind,
            x.CompletedAtUtc
        });

        builder.HasOne<EmbeddingProfile>()
            .WithMany()
            .HasForeignKey(x => x.EmbeddingProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
