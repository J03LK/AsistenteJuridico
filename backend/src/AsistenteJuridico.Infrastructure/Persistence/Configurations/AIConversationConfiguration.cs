using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

public class AIConversationConfiguration : IEntityTypeConfiguration<AIConversation>
{
    public void Configure(EntityTypeBuilder<AIConversation> builder)
    {
        builder.ToTable("ai_conversations");

        builder.HasKey(c => c.Id);

        // Control de concurrencia optimista nativo PostgreSQL (xmin)
        builder.Property(c => c.Version)
            .IsRowVersion();

        builder.Property(c => c.Titulo)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(c => c.CasoUso)
            .IsRequired();

        builder.Property(c => c.IsArchived)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(c => c.IsDeleted)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(c => c.DeletedBy)
            .HasMaxLength(100);

        builder.Property(c => c.DeletedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(c => c.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.Property(c => c.UpdatedAt)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        // Relación con Tenant
        builder.HasOne(c => c.Tenant)
            .WithMany()
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Clave candidata compuesta para FKs tenant-aware
        builder.HasIndex(c => new { c.TenantId, c.Id })
            .IsUnique();

        // Relación FK compuesta tenant-aware con Usuario (creador)
        builder.HasOne(c => c.Usuario)
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.UsuarioId })
            .HasPrincipalKey(u => new { u.TenantId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Relación FK compuesta tenant-aware con Expediente (opcional)
        builder.HasOne(c => c.Expediente)
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.ExpedienteId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.SetNull);

        // Índices estratégicos
        builder.HasIndex(c => new { c.TenantId, c.UsuarioId });
        builder.HasIndex(c => new { c.TenantId, c.ExpedienteId });
        builder.HasIndex(c => new { c.TenantId, c.CreatedAt });
        builder.HasIndex(c => new { c.TenantId, c.IsDeleted });
    }
}
