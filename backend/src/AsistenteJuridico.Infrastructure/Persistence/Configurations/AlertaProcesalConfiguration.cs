using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

public class AlertaProcesalConfiguration : IEntityTypeConfiguration<AlertaProcesal>
{
    public void Configure(EntityTypeBuilder<AlertaProcesal> builder)
    {
        builder.ToTable("alertas_procesales");

        builder.HasKey(a => a.Id);

        // Control de concurrencia optimista nativo PostgreSQL (mapeado automáticamente al system column xmin)
        builder.Property(a => a.Version)
            .IsRowVersion();

        builder.Property(a => a.Titulo)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(a => a.Mensaje)
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(a => a.MotivoResolucion)
            .HasMaxLength(500);

        builder.Property(a => a.FechaObjetivoUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(a => a.FechaDisparoUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(a => a.FechaLeidaUtc)
            .HasColumnType("timestamp with time zone");

        builder.Property(a => a.ResueltaUtc)
            .HasColumnType("timestamp with time zone");

        builder.Property(a => a.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        // Relación con Tenant
        builder.HasOne(a => a.Tenant)
            .WithMany()
            .HasForeignKey(a => a.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Relación FK compuesta tenant-aware con Usuario (destinatario opcional)
        builder.HasOne(a => a.Usuario)
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.UsuarioId })
            .HasPrincipalKey(u => new { u.TenantId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Relación FK compuesta tenant-aware con Expediente (contexto opcional)
        builder.HasOne(a => a.Expediente)
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.ExpedienteId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // 1. Índice de idempotencia histórica para alertas dirigidas a un usuario específico
        // EstadoResolucion != 3 (InvalidaPorReprogramacion)
        builder.HasIndex(a => new { a.TenantId, a.TipoOrigen, a.OrigenId, a.ReglaAlerta, a.UsuarioId, a.FechaObjetivoUtc })
            .HasDatabaseName("IX_alertas_idempotencia_historica_usuario")
            .HasFilter("\"EstadoResolucion\" != 3 AND \"UsuarioId\" IS NOT NULL")
            .IsUnique();

        // 2. Índice de idempotencia histórica para alertas institucionales de supervisión del estudio (UsuarioId IS NULL)
        builder.HasIndex(a => new { a.TenantId, a.TipoOrigen, a.OrigenId, a.ReglaAlerta, a.FechaObjetivoUtc })
            .HasDatabaseName("IX_alertas_idempotencia_historica_general")
            .HasFilter("\"EstadoResolucion\" != 3 AND \"UsuarioId\" IS NULL")
            .IsUnique();

        // 3. Índice para consultas rápidas de campana y bandeja de usuario
        builder.HasIndex(a => new { a.TenantId, a.UsuarioId, a.EstadoResolucion, a.Leida })
            .HasDatabaseName("IX_alertas_tenant_usuario_activas")
            .HasFilter("\"EstadoResolucion\" = 1");

        // 4. Índices para invalidación y búsqueda por origen y expediente
        builder.HasIndex(a => new { a.TenantId, a.OrigenId, a.TipoOrigen });
        builder.HasIndex(a => new { a.TenantId, a.ExpedienteId });
    }
}
