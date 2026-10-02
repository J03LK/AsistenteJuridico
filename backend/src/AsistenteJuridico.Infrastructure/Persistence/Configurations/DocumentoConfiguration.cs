using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

public class DocumentoConfiguration : IEntityTypeConfiguration<Documento>
{
    public void Configure(EntityTypeBuilder<Documento> builder)
    {
        builder.ToTable("documentos");

        builder.HasKey(d => d.Id);

        // Concurrencia optimista nativa PostgreSQL sobre metadatos del documento (mapeada automáticamente a xmin)
        builder.Property(d => d.Version).IsRowVersion();

        builder.Property(d => d.Titulo)
            .IsRequired()
            .HasMaxLength(250);

        builder.Property(d => d.TipoDocumento)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(d => d.RutaAlmacenamiento)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(d => d.ContentType)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(d => d.HashSha256)
            .HasMaxLength(64);

        builder.Property(d => d.MetadatosJson)
            .HasColumnType("jsonb");

        builder.Property(d => d.CreatedBy).HasMaxLength(100);
        builder.Property(d => d.UpdatedBy).HasMaxLength(100);
        builder.Property(d => d.DeletedBy).HasMaxLength(100);

        builder.Property(d => d.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.Property(d => d.UpdatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(d => d.DeletedAt)
            .HasColumnType("timestamp with time zone");

        // Relación con Tenant
        builder.HasOne(d => d.Tenant)
            .WithMany()
            .HasForeignKey(d => d.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Relación tenant-aware con Expediente (FK compuesta)
        builder.HasOne(d => d.Expediente)
            .WithMany(e => e.Documentos)
            .HasForeignKey(d => new { d.TenantId, d.ExpedienteId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Índices estratégicos
        builder.HasIndex(d => new { d.TenantId, d.ExpedienteId })
            .HasFilter("\"IsDeleted\" = false");

        builder.HasIndex(d => new { d.TenantId, d.EstadoIa });
        builder.HasIndex(d => new { d.TenantId, d.TipoDocumento });
    }
}
