using ICEHOTT.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ICEHOTT.Persistence.Configurations;

public sealed class ConversationMessageCitationConfiguration : IEntityTypeConfiguration<ConversationMessageCitation>
{
    public void Configure(EntityTypeBuilder<ConversationMessageCitation> builder)
    {
        builder.ToTable("conversation_message_citations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(200).IsRequired();
        builder.Property(x => x.SourceName).HasMaxLength(260);
        builder.HasIndex(x => new { x.WorkspaceId, x.MessageId });

        builder.HasOne<ConversationMessage>().WithMany()
            .HasForeignKey(x => new { x.MessageId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
