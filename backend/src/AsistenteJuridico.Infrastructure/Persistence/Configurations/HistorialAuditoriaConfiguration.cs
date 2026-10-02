using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

public class HistorialAuditoriaConfiguration : IEntityTypeConfiguration<HistorialAuditoria>
{
    public void Configure(EntityTypeBuilder<HistorialAuditoria> builder)
    {
        builder.ToTable("historial_auditorias");

        builder.HasKey(h => h.Id);

        builder.Property(h => h.Entidad)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(h => h.EntidadId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(h => h.Accion)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(h => h.ValoresAnterioresJson)
            .HasColumnType("jsonb");

        builder.Property(h => h.ValoresNuevosJson)
            .HasColumnType("jsonb");

        builder.Property(h => h.UsuarioEmail)
            .HasMaxLength(150);

        builder.Property(h => h.IpAddress)
            .HasMaxLength(50);

        builder.Property(h => h.Fecha)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        // Índices de consulta de trazabilidad
        builder.HasIndex(h => new { h.TenantId, h.Entidad, h.EntidadId });
        builder.HasIndex(h => new { h.TenantId, h.Fecha });
    }
}
