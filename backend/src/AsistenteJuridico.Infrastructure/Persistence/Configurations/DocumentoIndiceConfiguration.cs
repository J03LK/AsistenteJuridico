using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

/// <summary>
/// Fase 8.1 — documento_indices (contrato §6.1). FK compuestas tenant-aware (un índice nunca apunta a un documento
/// o expediente de otro tenant), unicidades parciales (un vigente y una construcción en curso por documento y
/// perfil), dimensión fija como invariante de la base y xmin para el lease y la confirmación.
/// </summary>
public class DocumentoIndiceConfiguration : IEntityTypeConfiguration<DocumentoIndice>
{
    public void Configure(EntityTypeBuilder<DocumentoIndice> builder)
    {
        builder.ToTable("documento_indices", t =>
        {
            t.HasCheckConstraint("CK_documento_indices_Dimensiones", $"\"Dimensiones\" = {DocumentoIndice.DimensionesPerfilInicial}");
            t.HasCheckConstraint("CK_documento_indices_HashContenido_formato",
                "\"HashContenido\" IS NULL OR \"HashContenido\" ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("CK_documento_indices_Estado", "\"Estado\" BETWEEN 0 AND 5");
            t.HasCheckConstraint("CK_documento_indices_Contadores",
                "\"Intentos\" >= 0 AND \"Fragmentos\" >= 0 AND \"TokensTotales\" >= 0");
        });

        builder.HasKey(i => i.Id);

        builder.Property(i => i.Version).IsRowVersion();

        builder.Property(i => i.Perfil).IsRequired().HasMaxLength(200);
        builder.Property(i => i.Estado).HasConversion<short>();
        builder.Property(i => i.HashContenido).HasColumnType("character(64)");
        builder.Property(i => i.ProcesadoPor).HasMaxLength(100);
        builder.Property(i => i.CodigoError).HasMaxLength(64);

        builder.Property(i => i.ProcesandoDesde).HasColumnType("timestamp with time zone");
        builder.Property(i => i.ProximoIntentoEn).HasColumnType("timestamp with time zone");
        builder.Property(i => i.IndexadoEn).HasColumnType("timestamp with time zone");
        builder.Property(i => i.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.Property(i => i.UpdatedAt).HasColumnType("timestamp with time zone");

        // Clave alternativa (TenantId, Id): destino de la FK compuesta de los fragmentos.
        builder.HasAlternateKey(i => new { i.TenantId, i.Id });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(i => i.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // (TenantId, DocumentoId) -> documentos(TenantId, Id). Crea la clave alternativa (TenantId, Id) en documentos.
        builder.HasOne(i => i.Documento)
            .WithMany()
            .HasForeignKey(i => new { i.TenantId, i.DocumentoId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(i => i.Expediente)
            .WithMany()
            .HasForeignKey(i => new { i.TenantId, i.ExpedienteId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        var indexado = (short)EstadoIndexacion.Indexado;
        var pendiente = (short)EstadoIndexacion.Pendiente;
        var procesando = (short)EstadoIndexacion.Procesando;

        // Dos índices sobre las mismas columnas: deben tener nombre propio en EF (si no, el segundo sustituye al primero).
        builder.HasIndex(i => new { i.DocumentoId, i.Perfil }, "UX_documento_indices_Documento_Perfil_Vigente")
            .IsUnique()
            .HasFilter($"\"Estado\" = {indexado}");

        builder.HasIndex(i => new { i.DocumentoId, i.Perfil }, "UX_documento_indices_Documento_Perfil_EnCurso")
            .IsUnique()
            .HasFilter($"\"Estado\" IN ({pendiente}, {procesando})");

        builder.HasIndex(i => new { i.Estado, i.ProximoIntentoEn })
            .HasDatabaseName("IX_documento_indices_Estado_ProximoIntentoEn");

        builder.HasIndex(i => new { i.TenantId, i.ExpedienteId, i.Perfil })
            .HasFilter($"\"Estado\" = {indexado}")
            .HasDatabaseName("IX_documento_indices_Tenant_Expediente_Perfil_Indexado");
    }
}
