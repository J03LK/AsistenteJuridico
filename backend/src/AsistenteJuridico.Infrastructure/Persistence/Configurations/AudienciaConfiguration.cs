using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

public class AudienciaConfiguration : IEntityTypeConfiguration<Audiencia>
{
    public void Configure(EntityTypeBuilder<Audiencia> builder)
    {
        builder.ToTable("audiencias");

        builder.HasKey(a => a.Id);

        // Concurrencia optimista nativa PostgreSQL (mapeada automáticamente a xmin)
        builder.Property(a => a.Version).IsRowVersion();

        builder.Property(a => a.FechaHora)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(a => a.SalaOVirtual)
            .IsRequired()
            .HasMaxLength(250);

        builder.Property(a => a.Notas)
            .HasMaxLength(2000);

        builder.Property(a => a.CreatedBy).HasMaxLength(100);
        builder.Property(a => a.UpdatedBy).HasMaxLength(100);

        builder.Property(a => a.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.Property(a => a.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        // Relación con Tenant
        builder.HasOne(a => a.Tenant)
            .WithMany()
            .HasForeignKey(a => a.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Relación tenant-aware con Expediente (FK compuesta)
        builder.HasOne(a => a.Expediente)
            .WithMany(e => e.Audiencias)
            .HasForeignKey(a => new { a.TenantId, a.ExpedienteId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Relación tenant-aware con ProcesoJudicial (opcional)
        builder.HasOne(a => a.ProcesoJudicial)
            .WithMany(p => p.Audiencias)
            .HasForeignKey(a => new { a.TenantId, a.ProcesoJudicialId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.SetNull);

        // Índices para calendario y agenda
        builder.HasIndex(a => new { a.TenantId, a.FechaHora, a.Estado });
        builder.HasIndex(a => new { a.TenantId, a.ExpedienteId });
        builder.HasIndex(a => new { a.TenantId, a.ProcesoJudicialId });
    }
}
