using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

public class AIUsageLogConfiguration : IEntityTypeConfiguration<AIUsageLog>
{
    public void Configure(EntityTypeBuilder<AIUsageLog> builder)
    {
        // Fase 8.1 (contrato §14.1): una operación de usuario siempre lleva su UsuarioId; Worker y Sistema llevan
        // ActorSistema y nunca UsuarioId. La nulabilidad no puede ocultar el actor de una operación interactiva.
        builder.ToTable("ai_usage_logs", t => t.HasCheckConstraint("CK_ai_usage_logs_Actor",
            "(\"Origen\" = 1 AND \"UsuarioId\" IS NOT NULL AND \"ActorSistema\" IS NULL) OR " +
            "(\"Origen\" IN (2, 3) AND \"UsuarioId\" IS NULL AND \"ActorSistema\" IS NOT NULL)"));

        builder.HasKey(l => l.Id);

        // Valor por defecto constante: añadir la columna es solo un cambio de metadatos en PostgreSQL (sin UPDATE de
        // filas), compatible con los triggers que impiden modificar ai_usage_logs. Las filas existentes quedan como Usuario.
        builder.Property(l => l.Origen)
            .HasConversion<short>()
            .IsRequired()
            .HasDefaultValue(Domain.Enums.OrigenUsoIA.Usuario)
            .HasSentinel((Domain.Enums.OrigenUsoIA)0);

        builder.Property(l => l.ActorSistema)
            .HasMaxLength(100);

        builder.Property(l => l.CasoUso)
            .IsRequired();

        builder.Property(l => l.ProviderId)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(l => l.ModelId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(l => l.TokensEntrada)
            .IsRequired();

        builder.Property(l => l.TokensSalida)
            .IsRequired();

        builder.Property(l => l.TotalTokens)
            .IsRequired();

        builder.Property(l => l.DuracionMs)
            .IsRequired();

        builder.Property(l => l.CostoEstimadoUsd)
            .HasPrecision(18, 6);

        builder.Property(l => l.Exitoso)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(l => l.CodigoError)
            .HasMaxLength(100);

        builder.Property(l => l.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        // Relación con Tenant
        builder.HasOne(l => l.Tenant)
            .WithMany()
            .HasForeignKey(l => l.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Relación FK compuesta tenant-aware con Usuario. Opcional desde la Fase 8.1 (Worker/Sistema sin usuario):
        // con UsuarioId NULL la FK compuesta no se comprueba (MATCH SIMPLE).
        builder.HasOne(l => l.Usuario)
            .WithMany()
            .HasForeignKey(l => new { l.TenantId, l.UsuarioId })
            .HasPrincipalKey(u => new { u.TenantId, u.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // Desacoplar relación de clave foránea con Conversación para preservar inmutabilidad estricta:
        // ConversationId se mantiene como una REFERENCIA HISTÓRICA inmutable.
        // Se ignora la propiedad de navegación en EF Core (builder.Ignore(l => l.Conversation))
        // para garantizar que la purga física de AIConversation NO genere ningún UPDATE ni DELETE
        // sobre ai_usage_logs, cumpliendo así con los triggers PostgreSQL (trg_ai_usage_logs_prevent_update
        // y trg_ai_usage_logs_prevent_delete). No se utiliza ConversationId para resolver autorización ni acceso.
        builder.Ignore(l => l.Conversation);
        builder.HasIndex(l => new { l.TenantId, l.ConversationId });

        // Índices estratégicos
        builder.HasIndex(l => new { l.TenantId, l.UsuarioId, l.CreatedAt });
        builder.HasIndex(l => new { l.TenantId, l.CreatedAt });
        builder.HasIndex(l => new { l.TenantId, l.CasoUso });
    }
}
