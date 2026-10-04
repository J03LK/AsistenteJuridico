using AsistenteJuridico.Application.Common.Security;
using AsistenteJuridico.Application.Features.AI.DTOs;
using AsistenteJuridico.Domain.Common;
using AsistenteJuridico.Domain.Entities;
using AsistenteJuridico.Domain.Enums;
using AsistenteJuridico.Domain.Tests.Fase6;
using AsistenteJuridico.Domain.Tests.Fase6X;
using AsistenteJuridico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AsistenteJuridico.Domain.Tests.Fase8;

/// <summary>
/// Fase 8.1 — Infraestructura y modelo del índice semántico sobre PostgreSQL real (imagen con pgvector):
/// esquema, invariante de dimensión en la base, unicidades parciales, aislamiento por tenant, xmin y reglas de actor
/// de AIUsageLog. Contrato docs/FASE_8_CONTRATO.md v1.1 (§3.2.1, §6, §14.1).
/// </summary>
public class Fase81InfraestructuraModeloTests : IAsyncLifetime
{
    private const string Perfil = "openai:text-embedding-3-small@1536|chunk-v1|ext-v1|norm-v1";
    private readonly Escenario6X _e = new();
    private Guid _documentoId;

    public async Task InitializeAsync()
    {
        await _e.SembrarAsync();
        _documentoId = await _e.DocumentoCrudoAsync($"{_e.TenantId:N}/{_e.ExpedienteId:N}/f81.txt", "text/plain; charset=utf-8");
    }

    public Task DisposeAsync()
    {
        _e.Dispose();
        return Task.CompletedTask;
    }

    private static float[] Vector(int dimensiones, float valor = 0.01f) => Enumerable.Repeat(valor, dimensiones).ToArray();

    private static string Hash(char c = 'a') => new(c, 64);

    private DocumentoIndice NuevoIndice(EstadoIndexacion estado = EstadoIndexacion.Pendiente, Guid? tenantId = null, Guid? documentoId = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId ?? _e.TenantId,
        DocumentoId = documentoId ?? _documentoId,
        ExpedienteId = _e.ExpedienteId,
        Perfil = Perfil,
        Estado = estado
    };

    private DocumentoFragmento NuevoFragmento(DocumentoIndice indice, string texto, float[] embedding, int orden = 0) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = indice.TenantId,
        DocumentoId = indice.DocumentoId,
        ExpedienteId = indice.ExpedienteId,
        IndiceId = indice.Id,
        Orden = orden,
        Texto = texto,
        Ubicacion = "{\"tipo\":\"lineas\",\"desde\":1,\"hasta\":3,\"etiqueta\":\"líneas 1–3\"}",
        CaracterInicio = 0,
        CaracterFin = texto.Length,
        TokensEstimados = texto.Length / 4,
        HashFragmento = Hash('b'),
        Embedding = embedding
    };

    private async Task GuardarAsync(params object[] entidades)
    {
        await using var context = _e.Contexto();
        context.AddRange(entidades);
        await context.SaveChangesAsync();
    }

    private static async Task<T?> EscalarAsync<T>(string sql, params (string Nombre, object Valor)[] parametros)
    {
        await using var conexion = new NpgsqlConnection(TestConfiguration.PostgresConnectionString);
        await conexion.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conexion);
        foreach (var (nombre, valor) in parametros)
        {
            cmd.Parameters.AddWithValue(nombre, valor);
        }

        var resultado = await cmd.ExecuteScalarAsync();
        return resultado is null or DBNull ? default : (T)resultado;
    }

    private static async Task<string> SqlStateDeAsync(Func<Task> accion)
    {
        var error = await Record.ExceptionAsync(accion);
        var postgres = error as PostgresException ?? error?.InnerException as PostgresException;
        Assert.NotNull(postgres);
        return postgres!.SqlState;
    }

    // ── Infraestructura y esquema ────────────────────────────────────────

    [Fact]
    public async Task Extensiones_VectorYUnaccent_Instaladas()
    {
        var vector = await EscalarAsync<string>("SELECT extversion FROM pg_extension WHERE extname = 'vector'");
        Assert.NotNull(vector);
        Assert.StartsWith("0.8.", vector);
        Assert.NotNull(await EscalarAsync<string>("SELECT extversion FROM pg_extension WHERE extname = 'unaccent'"));
    }

    [Fact]
    public async Task Esquema_Embedding_EsVector1536_YTextoBusquedaEsColumnaGeneradaConGin()
    {
        Assert.Equal("vector(1536)", await EscalarAsync<string>(
            "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'documento_fragmentos'::regclass AND attname = 'Embedding'"));
        Assert.Equal("s", (await EscalarAsync<char>(
            "SELECT attgenerated FROM pg_attribute WHERE attrelid = 'documento_fragmentos'::regclass AND attname = 'TextoBusqueda'")).ToString());
        Assert.Equal("gin", await EscalarAsync<string>(
            "SELECT am.amname FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid JOIN pg_am am ON am.oid = c.relam " +
            "WHERE i.indrelid = 'documento_fragmentos'::regclass AND c.relname = 'IX_documento_fragmentos_TextoBusqueda'"));
    }

    [Theory]
    [InlineData("documento_indices", "FK_documento_indices_documentos_TenantId_DocumentoId", 'r')]
    [InlineData("documento_indices", "FK_documento_indices_expedientes_TenantId_ExpedienteId", 'r')]
    [InlineData("documento_fragmentos", "FK_documento_fragmentos_documentos_TenantId_DocumentoId", 'r')]
    [InlineData("documento_fragmentos", "FK_documento_fragmentos_expedientes_TenantId_ExpedienteId", 'r')]
    [InlineData("documento_fragmentos", "FK_documento_fragmentos_documento_indices_TenantId_IndiceId", 'c')]
    public async Task Esquema_FkCompuestasConTenantId(string tabla, string fk, char borrado)
    {
        Assert.Equal(2, await EscalarAsync<int>(
            "SELECT cardinality(conkey) FROM pg_constraint WHERE conrelid = @t::regclass AND conname = @fk", ("t", tabla), ("fk", fk)));
        Assert.Equal(borrado.ToString(), (await EscalarAsync<char>(
            "SELECT confdeltype FROM pg_constraint WHERE conrelid = @t::regclass AND conname = @fk", ("t", tabla), ("fk", fk))).ToString());
        Assert.Contains("\"TenantId\"", await EscalarAsync<string>(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = @t::regclass AND conname = @fk", ("t", tabla), ("fk", fk)));
    }

    [Fact]
    public async Task Esquema_ClaveAlternativaTenantIdIdEnDocumentos()
    {
        Assert.Equal("UNIQUE (\"TenantId\", \"Id\")", await EscalarAsync<string>(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'documentos'::regclass AND conname = 'AK_documentos_TenantId_Id'"));
    }

    [Fact]
    public async Task Esquema_AIUsageLog_UsuarioIdNullable_OrigenConDefault_ActorSistema_YFuentesJson()
    {
        Assert.Equal("YES", await EscalarAsync<string>(
            "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'ai_usage_logs' AND column_name = 'UsuarioId'"));
        // Sin valor por defecto: un NULL de Worker/Sistema nunca se convierte en Guid.Empty.
        Assert.Null(await EscalarAsync<string>(
            "SELECT column_default FROM information_schema.columns WHERE table_name = 'ai_usage_logs' AND column_name = 'UsuarioId'"));
        Assert.Equal("1", await EscalarAsync<string>(
            "SELECT column_default FROM information_schema.columns WHERE table_name = 'ai_usage_logs' AND column_name = 'Origen'"));
        Assert.Equal(100, await EscalarAsync<int>(
            "SELECT character_maximum_length FROM information_schema.columns WHERE table_name = 'ai_usage_logs' AND column_name = 'ActorSistema'"));
        Assert.Equal("jsonb", await EscalarAsync<string>(
            "SELECT data_type FROM information_schema.columns WHERE table_name = 'ai_messages' AND column_name = 'FuentesJson'"));
        // Las filas anteriores a la 8.1 quedaron como Usuario (default de solo metadatos, sin UPDATE).
        Assert.Equal(0L, await EscalarAsync<long>("SELECT count(*) FROM ai_usage_logs WHERE \"Origen\" IS NULL"));
    }

    // ── Dimensión: invariante de PostgreSQL (§3.2.1) ─────────────────────

    [Theory]
    [InlineData(1535)]
    [InlineData(1537)]
    public async Task Dimension_PostgreSqlRechazaVectoresDeOtraDimension_PorSql(int dimensiones)
    {
        var indice = NuevoIndice();
        await GuardarAsync(indice);
        var literal = "[" + string.Join(",", Enumerable.Repeat("0.1", dimensiones)) + "]";

        var estado = await SqlStateDeAsync(async () =>
        {
            await using var conexion = new NpgsqlConnection(TestConfiguration.PostgresConnectionString);
            await conexion.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO documento_fragmentos (\"Id\",\"TenantId\",\"DocumentoId\",\"ExpedienteId\",\"IndiceId\",\"Orden\",\"Texto\",\"Ubicacion\"," +
                "\"CaracterInicio\",\"CaracterFin\",\"TokensEstimados\",\"HashFragmento\",\"Embedding\") VALUES " +
                "(gen_random_uuid(), @t, @d, @e, @i, 0, 'x', '{}', 0, 1, 0, @h, @v::vector)", conexion);
            cmd.Parameters.AddWithValue("t", indice.TenantId);
            cmd.Parameters.AddWithValue("d", indice.DocumentoId);
            cmd.Parameters.AddWithValue("e", indice.ExpedienteId);
            cmd.Parameters.AddWithValue("i", indice.Id);
            cmd.Parameters.AddWithValue("h", Hash('c'));
            cmd.Parameters.AddWithValue("v", literal);
            await cmd.ExecuteNonQueryAsync();
        });

        Assert.Equal("22000", estado);   // pgvector: "expected 1536 dimensions"
    }

    [Fact]
    public async Task Dimension_PostgreSqlRechazaVectorIncorrecto_AunqueVengaDeEf()
    {
        var indice = NuevoIndice();
        await GuardarAsync(indice);

        var estado = await SqlStateDeAsync(() => GuardarAsync(NuevoFragmento(indice, "texto", Vector(1535))));

        Assert.Equal("22000", estado);
    }

    [Fact]
    public async Task Dimension_CheckDelIndiceRechazaOtraDimension()
    {
        var indice = NuevoIndice();
        indice.Dimensiones = 1024;

        Assert.Equal("23514", await SqlStateDeAsync(() => GuardarAsync(indice)));
    }

    [Fact]
    public async Task Fragmento_EfIdaYVuelta_VectorExacto_YTextoBusquedaSinAcentosEnEspanol()
    {
        var indice = NuevoIndice(EstadoIndexacion.Indexado);
        var embedding = Enumerable.Range(0, 1536).Select(i => i / 1536f).ToArray();
        var fragmento = NuevoFragmento(indice, "La acción de amparo fue admitida por la jueza.", embedding);
        await GuardarAsync(indice, fragmento);

        await using var context = _e.Contexto();
        var leido = await context.DocumentoFragmentos.AsNoTracking().SingleAsync(f => f.Id == fragmento.Id);
        Assert.Equal(embedding, leido.Embedding);

        // Columna generada con unaccent + configuración spanish: "accion" (sin tilde) encuentra "acción".
        Assert.True(await EscalarAsync<bool>(
            "SELECT \"TextoBusqueda\" @@ websearch_to_tsquery('spanish', public.f_unaccent_es('accion amparo')) FROM documento_fragmentos WHERE \"Id\" = @id",
            ("id", fragmento.Id)));
        // Distancia coseno disponible (operador <=> de pgvector) sobre la columna tipada.
        Assert.Equal(0d, await EscalarAsync<double>(
            "SELECT \"Embedding\" <=> \"Embedding\" FROM documento_fragmentos WHERE \"Id\" = @id", ("id", fragmento.Id)), 6);
    }

    // ── Unicidades parciales (§6.1, §7.1, §16) ──────────────────────────

    [Fact]
    public async Task UnicidadParcial_DosIndicesVigentesDelMismoDocumentoYPerfil_Rechazado()
    {
        await GuardarAsync(NuevoIndice(EstadoIndexacion.Indexado));
        Assert.Equal("23505", await SqlStateDeAsync(() => GuardarAsync(NuevoIndice(EstadoIndexacion.Indexado))));
    }

    [Fact]
    public async Task UnicidadParcial_DosConstruccionesEnCurso_Rechazado()
    {
        await GuardarAsync(NuevoIndice(EstadoIndexacion.Pendiente));
        Assert.Equal("23505", await SqlStateDeAsync(() => GuardarAsync(NuevoIndice(EstadoIndexacion.Procesando))));
    }

    [Fact]
    public async Task UnicidadParcial_UnVigenteMasUnaConstruccionEnCurso_Permitido_YVariosHistoricos()
    {
        // Reindexación: el vigente sigue sirviendo mientras se construye el nuevo; los Fallido/Obsoleto no compiten.
        await GuardarAsync(
            NuevoIndice(EstadoIndexacion.Indexado),
            NuevoIndice(EstadoIndexacion.Pendiente),
            NuevoIndice(EstadoIndexacion.Fallido),
            NuevoIndice(EstadoIndexacion.Fallido),
            NuevoIndice(EstadoIndexacion.Obsoleto));

        await using var context = _e.Contexto();
        Assert.Equal(5, await context.DocumentoIndices.CountAsync(i => i.DocumentoId == _documentoId));
    }

    [Fact]
    public async Task Check_HashContenidoYEstado()
    {
        var hashInvalido = NuevoIndice();
        hashInvalido.HashContenido = new string('Z', 64);
        Assert.Equal("23514", await SqlStateDeAsync(() => GuardarAsync(hashInvalido)));

        var valido = NuevoIndice();
        valido.HashContenido = Hash();
        await GuardarAsync(valido);
    }

    // ── Multi-tenant (§4) ────────────────────────────────────────────────

    [Fact]
    public async Task FkCompuesta_IndiceDeOtroTenantSobreDocumentoAjeno_Rechazado()
    {
        // TenantId del otro tenant + DocumentoId de este tenant: la FK (TenantId, DocumentoId) no existe.
        var indice = NuevoIndice(tenantId: _e.OtroTenantId);
        Assert.Equal("23503", await SqlStateDeAsync(async () =>
        {
            await using var context = _e.Contexto(_e.OtroTenantId);
            context.DocumentoIndices.Add(indice);
            await context.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task FkCompuesta_FragmentoDeOtroTenantSobreIndiceAjeno_Rechazado()
    {
        var indice = NuevoIndice();
        await GuardarAsync(indice);
        var fragmento = NuevoFragmento(indice, "texto", Vector(1536));
        fragmento.TenantId = _e.OtroTenantId;

        Assert.Equal("23503", await SqlStateDeAsync(async () =>
        {
            await using var context = _e.Contexto(_e.OtroTenantId);
            context.DocumentoFragmentos.Add(fragmento);
            await context.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task FiltroGlobal_OtroTenantNoVeIndicesNiFragmentos()
    {
        var indice = NuevoIndice(EstadoIndexacion.Indexado);
        await GuardarAsync(indice, NuevoFragmento(indice, "contenido reservado", Vector(1536)));

        await using var ajeno = _e.Contexto(_e.OtroTenantId);
        Assert.False(await ajeno.DocumentoIndices.AnyAsync(i => i.Id == indice.Id));
        Assert.False(await ajeno.DocumentoFragmentos.AnyAsync(f => f.IndiceId == indice.Id));

        await using var propio = _e.Contexto();
        Assert.True(await propio.DocumentoFragmentos.AnyAsync(f => f.IndiceId == indice.Id));
    }

    // ── Borrado y concurrencia ───────────────────────────────────────────

    [Fact]
    public async Task Purga_BorrarElIndiceBorraSusFragmentosEnCascada()
    {
        var indice = NuevoIndice(EstadoIndexacion.Obsoleto);
        await GuardarAsync(indice, NuevoFragmento(indice, "a", Vector(1536), 0), NuevoFragmento(indice, "b", Vector(1536), 1));

        await using (var context = _e.Contexto())
        {
            context.DocumentoIndices.Remove(await context.DocumentoIndices.SingleAsync(i => i.Id == indice.Id));
            await context.SaveChangesAsync();
        }

        Assert.Equal(0L, await EscalarAsync<long>("SELECT count(*) FROM documento_fragmentos WHERE \"IndiceId\" = @i", ("i", indice.Id)));
    }

    [Fact]
    public async Task Restrict_UnDocumentoConIndiceNoSePuedeBorrarFisicamente()
    {
        await GuardarAsync(NuevoIndice());
        Assert.Equal("23503", await SqlStateDeAsync(async () =>
        {
            await using var conexion = new NpgsqlConnection(TestConfiguration.PostgresConnectionString);
            await conexion.OpenAsync();
            await using var cmd = new NpgsqlCommand("DELETE FROM documentos WHERE \"Id\" = @id", conexion);
            cmd.Parameters.AddWithValue("id", _documentoId);
            await cmd.ExecuteNonQueryAsync();
        }));
    }

    [Fact]
    public async Task Xmin_ConflictoDeConcurrenciaEnElIndice()
    {
        var indice = NuevoIndice();
        await GuardarAsync(indice);

        await using var primero = _e.Contexto();
        await using var segundo = _e.Contexto();
        var a = await primero.DocumentoIndices.SingleAsync(i => i.Id == indice.Id);
        var b = await segundo.DocumentoIndices.SingleAsync(i => i.Id == indice.Id);

        a.Estado = EstadoIndexacion.Procesando;
        a.ProcesandoDesde = DateTime.UtcNow;
        await primero.SaveChangesAsync();

        b.Estado = EstadoIndexacion.Procesando;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => segundo.SaveChangesAsync());
    }

    // ── AIUsageLog: actor (§14.1) ────────────────────────────────────────
    // ai_usage_logs es inmutable (triggers): estas pruebas trabajan dentro de una transacción que SIEMPRE se revierte,
    // así que ninguna fila llega a confirmarse en la base de desarrollo (ni se borra ni se actualiza nada).

    private const string ProveedorPrueba = "proveedor-81-tx";

    private AIUsageLog Uso(OrigenUsoIA origen, Guid? usuarioId, string? actor) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = _e.TenantId,
        UsuarioId = usuarioId,
        Origen = origen,
        ActorSistema = actor,
        CasoUso = AICasoUso.IndexacionSemantica,
        ProviderId = ProveedorPrueba,
        ModelId = "modelo-81",
        TokensEntrada = 10,
        TotalTokens = 10,
        CostoEstimadoUsd = 0.000002m
    };

    /// <summary>Ejecuta la acción en un contexto con transacción explícita y la revierte siempre.</summary>
    private async Task EnTransaccionRevertidaAsync(Func<ApplicationDbContext, Task> accion)
    {
        await using var context = _e.Contexto();
        await using var transaccion = await context.Database.BeginTransactionAsync();
        try
        {
            await accion(context);
        }
        finally
        {
            await transaccion.RollbackAsync();
        }
    }

    private async Task<long> UsosConfirmadosDelTenantAsync() =>
        await EscalarAsync<long>("SELECT count(*) FROM ai_usage_logs WHERE \"TenantId\" = @t", ("t", _e.TenantId));

    public static TheoryData<OrigenUsoIA, bool, bool> CombinacionesInvalidas => new()
    {
        { OrigenUsoIA.Usuario, false, false },   // usuario sin UsuarioId
        { OrigenUsoIA.Usuario, true, true },     // usuario con ActorSistema
        { OrigenUsoIA.Worker, true, true },      // worker con UsuarioId
        { OrigenUsoIA.Worker, false, false },    // worker sin ActorSistema
        { OrigenUsoIA.Sistema, false, false }    // sistema sin ActorSistema
    };

    [Theory]
    [MemberData(nameof(CombinacionesInvalidas))]
    public async Task Actor_CombinacionInvalida_RechazadaPorElCheck(OrigenUsoIA origen, bool conUsuario, bool conActor)
    {
        var uso = Uso(origen, conUsuario ? _e.SeniorId : null, conActor ? "worker:indexacion-semantica" : null);

        Assert.Equal("23514", await SqlStateDeAsync(() => EnTransaccionRevertidaAsync(async context =>
        {
            context.AIUsageLogs.Add(uso);
            await context.SaveChangesAsync();
        })));
        Assert.Equal(0L, await UsosConfirmadosDelTenantAsync());
    }

    [Fact]
    public async Task Actor_CombinacionesValidas_UsuarioWorkerYSistema_SinConfirmarFilas()
    {
        await EnTransaccionRevertidaAsync(async context =>
        {
            context.AIUsageLogs.AddRange(
                Uso(OrigenUsoIA.Usuario, _e.SeniorId, null),
                Uso(OrigenUsoIA.Worker, null, "worker:indexacion-semantica"),
                Uso(OrigenUsoIA.Sistema, null, "system:prueba-81"));
            await context.SaveChangesAsync();

            // Dentro de la transacción las tres filas existen y respetan el CHECK.
            Assert.Equal(3, await context.AIUsageLogs.CountAsync(u => u.ProviderId == ProveedorPrueba));
        });

        Assert.Equal(0L, await UsosConfirmadosDelTenantAsync());   // nada confirmado
    }

    [Fact]
    public async Task Actor_FlujoExistenteDeIa_RegistraOrigenUsuarioConElUsuarioAutenticado()
    {
        var id = await _e.DocumentoAsync(Archivos.Txt("Hechos del caso para resumir."), "r.txt", "text/plain");

        await EnTransaccionRevertidaAsync(async context =>
        {
            // El servicio guarda con el mismo contexto, así que su SaveChanges participa en la transacción.
            var servicio = _e.Servicio(context, _e.SeniorId, Roles.AbogadoSenior, new ProveedorFijo());
            await servicio.SummarizeDocumentoAsync(new AISummarizeDocumentoDto(id));

            var uso = await context.AIUsageLogs.SingleAsync(u => u.UsuarioId == _e.SeniorId);
            Assert.Equal(OrigenUsoIA.Usuario, uso.Origen);
            Assert.Equal(_e.SeniorId, uso.UsuarioId);
            Assert.Null(uso.ActorSistema);
        });

        Assert.Equal(0L, await UsosConfirmadosDelTenantAsync());
    }

    [Fact]
    public async Task Consumo_UsuarioIdNullableNoRompeElInformeExistente()
    {
        // Regresión del cambio de tipo (Guid -> Guid?): el informe de la Fase 6 sigue funcionando con filas de usuario.
        await EnTransaccionRevertidaAsync(async context =>
        {
            context.AIUsageLogs.AddRange(Uso(OrigenUsoIA.Usuario, _e.SeniorId, null), Uso(OrigenUsoIA.Usuario, _e.SeniorId, null));
            await context.SaveChangesAsync();

            var admin = _e.Servicio(context, Guid.NewGuid(), Roles.AdminEstudio, new ProveedorFijo());
            var consumo = await admin.GetConsumoAsync(new AIConsumoQueryDto(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)));

            Assert.Equal(2, consumo.TotalInvocaciones);
            var porUsuario = Assert.Single(consumo.DesglosePorUsuario);
            Assert.Equal(_e.SeniorId, porUsuario.UsuarioId);
            Assert.Equal(2, porUsuario.Invocaciones);
        });

        Assert.Equal(0L, await UsosConfirmadosDelTenantAsync());
    }
}
