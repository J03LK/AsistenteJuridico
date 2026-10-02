using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

public class TareaConfiguration : IEntityTypeConfiguration<Tarea>
{
    public void Configure(EntityTypeBuilder<Tarea> builder)
    {
        builder.ToTable("tareas");

        builder.HasKey(t => t.Id);

        // Concurrencia optimista nativa PostgreSQL (mapeada automáticamente a xmin)
        builder.Property(t => t.Version).IsRowVersion();

        builder.Property(t => t.Titulo)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(t => t.Descripcion)
            .HasMaxLength(2000);

        builder.Property(t => t.FechaVencimiento)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(t => t.FechaCompletada)
            .HasColumnType("timestamp with time zone");

        builder.Property(t => t.CreatedBy).HasMaxLength(100);
        builder.Property(t => t.UpdatedBy).HasMaxLength(100);

        builder.Property(t => t.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.Property(t => t.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        // Relación con Tenant
        builder.HasOne(t => t.Tenant)
            .WithMany()
            .HasForeignKey(t => t.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Relación tenant-aware con Expediente
        builder.HasOne(t => t.Expediente)
            .WithMany(e => e.Tareas)
            .HasForeignKey(t => new { t.TenantId, t.ExpedienteId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Relación con Usuario asignado
        builder.HasOne(t => t.AsignadoA)
            .WithMany(u => u.TareasAsignadas)
            .HasForeignKey(t => t.AsignadoAUsuarioId)
            .OnDelete(DeleteBehavior.SetNull);

        // Índices para alertas de vencimiento y filtros
        builder.HasIndex(t => new { t.TenantId, t.Estado });
        builder.HasIndex(t => new { t.TenantId, t.FechaVencimiento });
        builder.HasIndex(t => new { t.TenantId, t.AsignadoAUsuarioId, t.FechaVencimiento, t.Estado });
        builder.HasIndex(t => new { t.TenantId, t.ExpedienteId });
    }
}
