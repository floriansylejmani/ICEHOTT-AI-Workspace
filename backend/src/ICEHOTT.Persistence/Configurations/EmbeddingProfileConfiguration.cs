using ICEHOTT.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class EmbeddingProfileConfiguration : IEntityTypeConfiguration<EmbeddingProfile>
{
    public void Configure(EntityTypeBuilder<EmbeddingProfile> builder)
    {
        builder.ToTable("embedding_profiles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Key).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Provider).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Model).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Version).HasMaxLength(80).IsRequired();
        builder.Property(x => x.DistanceMetric).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Normalization).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.HasIndex(x => x.Key).IsUnique();
        builder.HasIndex(x => new { x.Provider, x.Model, x.Version, x.IndexVersion }).IsUnique();
        builder.HasIndex(x => x.Status)
            .IsUnique()
            .HasFilter("\"Status\" = 'Active'");

        var baselineCreatedAt = new DateTimeOffset(
            2026, 9, 24, 0, 0, 0, TimeSpan.Zero);

        builder.HasData(new EmbeddingProfile(
            Guid.Parse("3f36a640-0d25-4a76-9d0d-640000000001"),
            "local-deterministic-64-v1",
            "icehott-ai-runtime",
            "deterministic-64d",
            64,
            "1",
            1,
            "cosine",
            "unit",
            EmbeddingProfileStatus.Active,
            baselineCreatedAt));
    }
}
