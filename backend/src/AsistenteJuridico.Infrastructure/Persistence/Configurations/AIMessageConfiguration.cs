using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

public class AIMessageConfiguration : IEntityTypeConfiguration<AIMessage>
{
    public void Configure(EntityTypeBuilder<AIMessage> builder)
    {
        builder.ToTable("ai_messages");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Rol)
            .IsRequired();

        builder.Property(m => m.Contenido)
            .IsRequired();

        builder.Property(m => m.SystemPromptVersion)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(m => m.ModelId)
            .HasMaxLength(100);

        builder.Property(m => m.ProviderId)
            .HasMaxLength(50);

        builder.Property(m => m.FinishReason)
            .HasMaxLength(50);

        builder.Property(m => m.Disclaimer)
            .HasMaxLength(1000);

        builder.Property(m => m.ContextoAutorizado)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(m => m.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        // Relación con Tenant
        builder.HasOne(m => m.Tenant)
            .WithMany()
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Relación FK compuesta tenant-aware con Conversación
        builder.HasOne(m => m.Conversation)
            .WithMany(c => c.Mensajes)
            .HasForeignKey(m => new { m.TenantId, m.ConversationId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Índices estratégicos
        builder.HasIndex(m => new { m.TenantId, m.ConversationId, m.CreatedAt });
    }
}
