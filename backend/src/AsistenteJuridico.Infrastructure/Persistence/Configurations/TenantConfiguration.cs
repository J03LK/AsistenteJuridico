using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Nombre)
            .IsRequired()
            .HasMaxLength(150);

        builder.Property(t => t.Ruc)
            .HasMaxLength(13);

        builder.Property(t => t.IdentificadorUrl)
            .IsRequired()
            .HasMaxLength(50);

        builder.HasIndex(t => t.IdentificadorUrl)
            .IsUnique();

        builder.Property(t => t.Plan)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(t => t.ZonaHorariaId)
            .IsRequired()
            .HasMaxLength(64)
            .HasDefaultValue("America/Guayaquil");

        builder.Property(t => t.ConfiguracionJson)
            .HasColumnType("jsonb");

        builder.Property(t => t.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.Property(t => t.UpdatedAt)
            .HasColumnType("timestamp with time zone");
    }
}
