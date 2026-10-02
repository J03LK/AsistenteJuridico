using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

public class ExpedienteConfiguration : IEntityTypeConfiguration<Expediente>
{
    public void Configure(EntityTypeBuilder<Expediente> builder)
    {
        builder.ToTable("expedientes");

        builder.HasKey(e => e.Id);

        // Concurrencia optimista nativa PostgreSQL (mapeada automáticamente a xmin)
        builder.Property(e => e.Version).IsRowVersion();

        builder.Property(e => e.NumeroExpediente)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(e => e.Titulo)
            .IsRequired()
            .HasMaxLength(250);

        builder.Property(e => e.Descripcion)
            .HasMaxLength(4000);

        builder.Property(e => e.Materia)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(e => e.CreatedBy).HasMaxLength(100);
        builder.Property(e => e.UpdatedBy).HasMaxLength(100);
        builder.Property(e => e.DeletedBy).HasMaxLength(100);

        builder.Property(e => e.FechaApertura)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.Property(e => e.FechaCierreEstimada)
            .HasColumnType("timestamp with time zone");

        builder.Property(e => e.FechaCierreReal)
            .HasColumnType("timestamp with time zone");

        builder.Property(e => e.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.Property(e => e.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(e => e.DeletedAt)
            .HasColumnType("timestamp with time zone");

        // Relación con Tenant
        builder.HasOne(e => e.Tenant)
            .WithMany(t => t.Expedientes)
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Clave candidata compuesta para FKs tenant-aware
        builder.HasIndex(e => new { e.TenantId, e.Id })
            .IsUnique();

        // Relación tenant-aware con Cliente (FK Compuesta que impide mezclar tenants a nivel de BD)
        builder.HasOne(e => e.Cliente)
            .WithMany(c => c.Expedientes)
            .HasForeignKey(e => new { e.TenantId, e.ClienteId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Relación con Abogado Responsable (Usuario)
        builder.HasOne(e => e.AbogadoResponsable)
            .WithMany(u => u.ExpedientesAsignados)
            .HasForeignKey(e => e.AbogadoResponsableId)
            .OnDelete(DeleteBehavior.SetNull);

        // Índices estratégicos
        builder.HasIndex(e => new { e.TenantId, e.NumeroExpediente })
            .IsUnique();

        builder.HasIndex(e => new { e.TenantId, e.Estado });
        builder.HasIndex(e => new { e.TenantId, e.ClienteId });
        builder.HasIndex(e => new { e.TenantId, e.AbogadoResponsableId });
    }
}
