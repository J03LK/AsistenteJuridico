using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

public class UsuarioConfiguration : IEntityTypeConfiguration<Usuario>
{
    public void Configure(EntityTypeBuilder<Usuario> builder)
    {
        builder.ToTable("usuarios");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.NombreCompleto)
            .IsRequired()
            .HasMaxLength(150);

        builder.Property(u => u.Email)
            .IsRequired()
            .HasMaxLength(150);

        builder.Property(u => u.Rol)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(u => u.CreatedBy).HasMaxLength(100);
        builder.Property(u => u.UpdatedBy).HasMaxLength(100);

        builder.Property(u => u.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.Property(u => u.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        // Relación con Tenant
        builder.HasOne(u => u.Tenant)
            .WithMany(t => t.Usuarios)
            .HasForeignKey(u => u.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Índice compuesto para email único por organización
        builder.HasIndex(u => new { u.TenantId, u.Email })
            .IsUnique();

        // Clave candidata compuesta para FKs tenant-aware
        builder.HasIndex(u => new { u.TenantId, u.Id })
            .IsUnique();
    }
}
