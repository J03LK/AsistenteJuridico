using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

public class ClienteConfiguration : IEntityTypeConfiguration<Cliente>
{
    public void Configure(EntityTypeBuilder<Cliente> builder)
    {
        builder.ToTable("clientes");

        builder.HasKey(c => c.Id);

        // Concurrencia optimista nativa PostgreSQL (mapeada automáticamente a xmin)
        builder.Property(c => c.Version).IsRowVersion();

        builder.Property(c => c.Identificacion)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(c => c.NombreRazonSocial)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(c => c.Email)
            .HasMaxLength(150);

        builder.Property(c => c.Telefono)
            .HasMaxLength(30);

        builder.Property(c => c.Direccion)
            .HasMaxLength(250);

        builder.Property(c => c.Notas)
            .HasMaxLength(1000);

        builder.Property(c => c.CreatedBy).HasMaxLength(100);
        builder.Property(c => c.UpdatedBy).HasMaxLength(100);
        builder.Property(c => c.DeletedBy).HasMaxLength(100);

        builder.Property(c => c.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.Property(c => c.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(c => c.DeletedAt)
            .HasColumnType("timestamp with time zone");

        // Relación con Tenant
        builder.HasOne(c => c.Tenant)
            .WithMany(t => t.Clientes)
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Clave candidata compuesta para Foreign Keys tenant-aware
        builder.HasIndex(c => new { c.TenantId, c.Id })
            .IsUnique();

        // Índice para búsqueda por nombre
        builder.HasIndex(c => new { c.TenantId, c.NombreRazonSocial });

        // Identificación única por tenant para registros no eliminados
        builder.HasIndex(c => new { c.TenantId, c.Identificacion })
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");
    }
}
