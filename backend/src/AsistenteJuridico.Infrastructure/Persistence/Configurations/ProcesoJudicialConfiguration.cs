using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

public class ProcesoJudicialConfiguration : IEntityTypeConfiguration<ProcesoJudicial>
{
    public void Configure(EntityTypeBuilder<ProcesoJudicial> builder)
    {
        builder.ToTable("procesos_judiciales");

        builder.HasKey(p => p.Id);

        // Concurrencia optimista nativa PostgreSQL (mapeada automáticamente a xmin)
        builder.Property(p => p.Version).IsRowVersion();

        builder.Property(p => p.NumeroProceso)
            .IsRequired()
            .HasMaxLength(30);

        builder.Property(p => p.Judicatura)
            .IsRequired()
            .HasMaxLength(250);

        builder.Property(p => p.JuezPonente)
            .HasMaxLength(150);

        builder.Property(p => p.AccionInfraccion)
            .HasMaxLength(150);

        builder.Property(p => p.Materia)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(p => p.EstadoJudicial)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(p => p.DetallesJson)
            .HasColumnType("jsonb");

        builder.Property(p => p.CreatedBy).HasMaxLength(100);
        builder.Property(p => p.UpdatedBy).HasMaxLength(100);

        builder.Property(p => p.FechaInicio)
            .HasColumnType("timestamp with time zone");

        builder.Property(p => p.UltimaSincronizacion)
            .HasColumnType("timestamp with time zone");

        builder.Property(p => p.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.Property(p => p.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        // Relación con Tenant
        builder.HasOne(p => p.Tenant)
            .WithMany()
            .HasForeignKey(p => p.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Clave candidata compuesta para FKs tenant-aware
        builder.HasIndex(p => new { p.TenantId, p.Id })
            .IsUnique();

        // Índice compuesto único por organización y número de proceso (Copia contextualizada por Tenant)
        builder.HasIndex(p => new { p.TenantId, p.NumeroProceso })
            .IsUnique();

        builder.HasIndex(p => new { p.TenantId, p.Judicatura });
        builder.HasIndex(p => new { p.TenantId, p.EstadoJudicial });
    }
}
