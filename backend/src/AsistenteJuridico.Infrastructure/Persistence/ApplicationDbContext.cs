using System.Linq.Expressions;
using AsistenteJuridico.Application.Common.Interfaces;
using AsistenteJuridico.Domain.Common;
using AsistenteJuridico.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AsistenteJuridico.Infrastructure.Persistence;

public class ApplicationDbContext : IdentityDbContext<Usuario, ApplicationRole, Guid>, IApplicationDbContext
{
    private readonly ICurrentTenantService? _currentTenantService;

    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options,
        ICurrentTenantService? currentTenantService = null)
        : base(options)
    {
        _currentTenantService = currentTenantService;
    }

    public Guid? CurrentTenantId => _currentTenantService?.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Cliente> Clientes => Set<Cliente>();
    public DbSet<Expediente> Expedientes => Set<Expediente>();
    public DbSet<ProcesoJudicial> ProcesosJudiciales => Set<ProcesoJudicial>();
    public DbSet<ExpedienteProcesoJudicial> ExpedienteProcesosJudiciales => Set<ExpedienteProcesoJudicial>();
    public DbSet<Documento> Documentos => Set<Documento>();
    public DbSet<Tarea> Tareas => Set<Tarea>();
    public DbSet<Audiencia> Audiencias => Set<Audiencia>();
    public DbSet<TenantSecuencia> TenantSecuencias => Set<TenantSecuencia>();
    public DbSet<HistorialAuditoria> HistorialAuditorias => Set<HistorialAuditoria>();
    public DbSet<AlertaProcesal> AlertasProcesales => Set<AlertaProcesal>();
    public DbSet<AIConversation> AIConversations => Set<AIConversation>();
    public DbSet<AIMessage> AIMessages => Set<AIMessage>();
    public DbSet<AIUsageLog> AIUsageLogs => Set<AIUsageLog>();
    public DbSet<DocumentoIndice> DocumentoIndices => Set<DocumentoIndice>();
    public DbSet<DocumentoFragmento> DocumentoFragmentos => Set<DocumentoFragmento>();

    /// <summary>
    /// Fase 8.1: el modelo de este contexto contiene una propiedad vector (documento_fragmentos.Embedding), así que
    /// TODO ApplicationDbContext sobre PostgreSQL necesita el plugin de pgvector para construir el modelo, aunque la
    /// operación no toque fragmentos (sin él, falla la construcción del modelo). Solo actúa cuando el plugin FALTA:
    /// el contexto de DI y los que reutilizan sus opciones (auditoría y consumo independientes) ya lo traen y no se
    /// tocan; lo añade a los construidos con opciones propias (pruebas). No afecta a InMemory (no relacional). Solo
    /// registra el mapeo de tipos: no ejecuta consultas ni vectoriza nada.
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var esRelacional = optionsBuilder.Options.Extensions
            .OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>().Any();
        var tienePgvector = optionsBuilder.Options.FindExtension<Pgvector.EntityFrameworkCore.VectorDbContextOptionsExtension>() != null;

        if (esRelacional && !tienePgvector)
        {
            optionsBuilder.UseNpgsql(npgsql => npgsql.UseVector());
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Fase 8.1: pgvector (vector) y unaccent (texto de búsqueda sin acentos). Las crea la migración Fase81.
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.HasPostgresExtension("unaccent");

        // Renombrar tablas de Identity a snake_case
        modelBuilder.Entity<Usuario>(b =>
        {
            b.ToTable("usuarios");
        });

        modelBuilder.Entity<ApplicationRole>(b =>
        {
            b.ToTable("roles");
        });

        modelBuilder.Entity<IdentityUserRole<Guid>>(b =>
        {
            b.ToTable("usuario_roles");
        });

        modelBuilder.Entity<IdentityUserClaim<Guid>>(b =>
        {
            b.ToTable("usuario_claims");
        });

        modelBuilder.Entity<IdentityRoleClaim<Guid>>(b =>
        {
            b.ToTable("role_claims");
        });

        modelBuilder.Entity<IdentityUserLogin<Guid>>(b =>
        {
            b.ToTable("usuario_logins");
        });

        modelBuilder.Entity<IdentityUserToken<Guid>>(b =>
        {
            b.ToTable("usuario_tokens");
        });

        // Aplicar configuraciones Fluent API del ensamblado
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        // Fase 8.1: tipos de pgvector y tsvector solo con PostgreSQL (InMemory no los admite).
        if (Database.IsNpgsql())
        {
            Configurations.DocumentoFragmentoConfiguration.ConfigurarPostgreSql(modelBuilder.Entity<DocumentoFragmento>());
        }

        // Aplicar filtros globales de consulta estrictos (Multi-Tenant y Soft Delete)
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            var isMultiTenant = typeof(IMultiTenant).IsAssignableFrom(clrType);
            var isSoftDeletable = typeof(ISoftDeletable).IsAssignableFrom(clrType);

            if (isMultiTenant || isSoftDeletable)
            {
                var parameter = Expression.Parameter(clrType, "e");
                Expression? combinedFilter = null;

                if (isMultiTenant)
                {
                    // AISLAMIENTO ESTRICTO:
                    // CurrentTenantId.HasValue && (Guid?)e.TenantId == CurrentTenantId
                    // Si CurrentTenantId es null, la expresión evalúa a false de forma segura y NUNCA retorna registros.
                    var tenantIdProp = Expression.Property(parameter, nameof(IMultiTenant.TenantId));
                    var nullableTenantId = Expression.Convert(tenantIdProp, typeof(Guid?));
                    var currentTenantProp = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));
                    var hasValueProp = Expression.Property(currentTenantProp, nameof(Nullable<Guid>.HasValue));

                    var tenantEquals = Expression.Equal(nullableTenantId, currentTenantProp);
                    var strictTenantFilter = Expression.AndAlso(hasValueProp, tenantEquals);

                    combinedFilter = strictTenantFilter;
                }

                if (isSoftDeletable)
                {
                    // !e.IsDeleted
                    var isDeletedProp = Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted));
                    var notDeleted = Expression.Equal(isDeletedProp, Expression.Constant(false));

                    combinedFilter = combinedFilter == null
                        ? notDeleted
                        : Expression.AndAlso(combinedFilter, notDeleted);
                }

                if (combinedFilter != null)
                {
                    var lambda = Expression.Lambda(combinedFilter, parameter);
                    modelBuilder.Entity(clrType).HasQueryFilter(lambda);
                }
            }
        }
    }
}
