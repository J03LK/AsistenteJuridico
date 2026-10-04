using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;
using Pgvector;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

/// <summary>
/// Fase 8.1 — documento_fragmentos (contrato §6.2). El embedding (float[] en el dominio) se guarda como
/// vector(1536): PostgreSQL rechaza cualquier otra dimensión (§3.2.1). El texto de búsqueda es una columna generada
/// (tsvector 'spanish' sin acentos) mapeada como propiedad sombra, para no exponer tipos de Npgsql al dominio.
/// Sin índice ANN en la Fase 8 (búsqueda exacta acotada a un expediente).
/// </summary>
public class DocumentoFragmentoConfiguration : IEntityTypeConfiguration<DocumentoFragmento>
{
    /// <summary>Columna generada; la función inmutable public.f_unaccent_es la crea la migración Fase81IndiceSemantico.</summary>
    public const string TextoBusqueda = "TextoBusqueda";

    public void Configure(EntityTypeBuilder<DocumentoFragmento> builder)
    {
        builder.ToTable("documento_fragmentos", t =>
        {
            t.HasCheckConstraint("CK_documento_fragmentos_HashFragmento_formato", "\"HashFragmento\" ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("CK_documento_fragmentos_Rango",
                "\"Orden\" >= 0 AND \"CaracterInicio\" >= 0 AND \"CaracterFin\" >= \"CaracterInicio\" AND \"TokensEstimados\" >= 0");
        });

        builder.HasKey(f => f.Id);

        builder.Property(f => f.Texto).IsRequired();
        builder.Property(f => f.Ubicacion).IsRequired().HasColumnType("jsonb");
        builder.Property(f => f.RutaSeccion).HasMaxLength(500);
        builder.Property(f => f.HashFragmento).IsRequired().HasColumnType("character(64)");
        builder.Property(f => f.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.Property(f => f.Embedding).IsRequired();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(f => f.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Documento>()
            .WithMany()
            .HasForeignKey(f => new { f.TenantId, f.DocumentoId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Expediente>()
            .WithMany()
            .HasForeignKey(f => new { f.TenantId, f.ExpedienteId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // El fragmento hereda el perfil de su índice y se borra en cascada con él (purga).
        builder.HasOne(f => f.Indice)
            .WithMany()
            .HasForeignKey(f => new { f.TenantId, f.IndiceId })
            .HasPrincipalKey(i => new { i.TenantId, i.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(f => new { f.IndiceId, f.Orden }).IsUnique();
        builder.HasIndex(f => new { f.TenantId, f.ExpedienteId, f.IndiceId });
    }

    /// <summary>
    /// Mapeo específico de PostgreSQL: embedding como vector(1536) y texto de búsqueda generado con su índice GIN.
    /// Solo con Npgsql: el proveedor InMemory de las pruebas no admite estos tipos, y con él el embedding queda como
    /// float[]. El esquema real y las migraciones siempre se generan con Npgsql.
    /// </summary>
    public static void ConfigurarPostgreSql(EntityTypeBuilder<DocumentoFragmento> builder)
    {
        builder.Property(f => f.Embedding)
            .HasColumnType($"vector({DocumentoIndice.DimensionesPerfilInicial})")
            .HasConversion(
                v => new Vector(v),
                v => v.ToArray(),
                new ValueComparer<float[]>(
                    (a, b) => ReferenceEquals(a, b) || (a != null && b != null && a.SequenceEqual(b)),
                    v => v.Aggregate(17, (hash, x) => HashCode.Combine(hash, x)),
                    v => v.ToArray()));

        builder.Property<NpgsqlTsVector>(TextoBusqueda)
            .HasColumnType("tsvector")
            .HasComputedColumnSql("to_tsvector('spanish'::regconfig, public.f_unaccent_es(\"Texto\"))", stored: true);

        builder.HasIndex(TextoBusqueda).HasMethod("GIN");
    }
}
