using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

public class TenantSecuenciaConfiguration : IEntityTypeConfiguration<TenantSecuencia>
{
    public void Configure(EntityTypeBuilder<TenantSecuencia> builder)
    {
        builder.ToTable("tenant_secuencias");

        builder.HasKey(ts => new { ts.TenantId, ts.TipoSecuencia, ts.Anio });

        builder.Property(ts => ts.TipoSecuencia)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(ts => ts.UltimoValor)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(ts => ts.UpdatedAt)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.HasOne(ts => ts.Tenant)
            .WithMany()
            .HasForeignKey(ts => ts.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
