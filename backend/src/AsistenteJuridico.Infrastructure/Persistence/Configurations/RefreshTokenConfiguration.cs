using AsistenteJuridico.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AsistenteJuridico.Infrastructure.Persistence.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");

        builder.HasKey(rt => rt.Id);

        builder.Property(rt => rt.TokenHash)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(rt => rt.SecurityStamp)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(rt => rt.CreatedByIp)
            .HasMaxLength(50);

        builder.Property(rt => rt.RevokedByIp)
            .HasMaxLength(50);

        builder.Property(rt => rt.ReplacedByTokenHash)
            .HasMaxLength(128);

        builder.Property(rt => rt.ReasonRevoked)
            .HasMaxLength(200);

        builder.Property(rt => rt.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder.Property(rt => rt.ExpiresAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(rt => rt.RevokedAt)
            .HasColumnType("timestamp with time zone");

        // Relación con Usuario
        builder.HasOne(rt => rt.User)
            .WithMany(u => u.RefreshTokens)
            .HasForeignKey(rt => rt.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Relación con Tenant
        builder.HasOne(rt => rt.Tenant)
            .WithMany(t => t.RefreshTokens)
            .HasForeignKey(rt => rt.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // Índices de búsqueda y seguridad
        builder.HasIndex(rt => rt.TokenHash)
            .IsUnique();

        builder.HasIndex(rt => rt.FamilyId);

        builder.HasIndex(rt => new { rt.UserId, rt.IsRevoked });
    }
}
