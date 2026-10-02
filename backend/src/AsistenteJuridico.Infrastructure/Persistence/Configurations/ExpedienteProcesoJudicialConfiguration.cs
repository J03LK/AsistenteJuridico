using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

public class ExpedienteProcesoJudicialConfiguration : IEntityTypeConfiguration<ExpedienteProcesoJudicial>
{
    public void Configure(EntityTypeBuilder<ExpedienteProcesoJudicial> builder)
    {
        builder.ToTable("expedientes_procesos_judiciales");

        builder.HasKey(ep => ep.Id);

        builder.Property(ep => ep.Observaciones)
            .HasMaxLength(500);

        builder.Property(ep => ep.FechaVinculacion)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        // Relación con Tenant
        builder.HasOne(ep => ep.Tenant)
            .WithMany()
            .HasForeignKey(ep => ep.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Relación tenant-aware con Expediente (FK compuesta)
        builder.HasOne(ep => ep.Expediente)
            .WithMany(e => e.ProcesosVinculados)
            .HasForeignKey(ep => new { ep.TenantId, ep.ExpedienteId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Relación tenant-aware con ProcesoJudicial (FK compuesta)
        builder.HasOne(ep => ep.ProcesoJudicial)
            .WithMany(p => p.ExpedientesVinculados)
            .HasForeignKey(ep => new { ep.TenantId, ep.ProcesoJudicialId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Unicidad para evitar vincular dos veces la misma causa al mismo expediente en un tenant
        builder.HasIndex(ep => new { ep.TenantId, ep.ExpedienteId, ep.ProcesoJudicialId })
            .IsUnique();

        // Restricción de base de datos: Máximo un proceso judicial principal por expediente
        builder.HasIndex(ep => new { ep.TenantId, ep.ExpedienteId })
            .HasFilter("\"EsPrincipal\" = TRUE")
            .IsUnique();
    }
}
