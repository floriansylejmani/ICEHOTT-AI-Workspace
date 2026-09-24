using ICEHOTT.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class ConversationMessageConfiguration : IEntityTypeConfiguration<ConversationMessage>
{
    public void Configure(EntityTypeBuilder<ConversationMessage> builder)
    {
        builder.ToTable("conversation_messages");
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.Id, x.WorkspaceId });
        builder.Property(x => x.Role)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();
        builder.Property(x => x.Content).HasMaxLength(12000).IsRequired();
        builder.HasIndex(x => new { x.WorkspaceId, x.ConversationId, x.CreatedAtUtc });

        builder.HasOne<Conversation>().WithMany()
            .HasForeignKey(x => new { x.ConversationId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
