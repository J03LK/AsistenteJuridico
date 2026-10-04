# Fase 8.3 — Contrato del proveedor de embeddings

| Campo | Valor |
|---|---|
| Versión | 1.2 |
| Estado | **Diseño — pendiente de revisión y aprobación. Implementación NO autorizada.** |
| Fecha | 2026-10-04 |
| Base | commit `6e0847b3f197a05462d359a04e1816265291abcd` (Fase 8.2 cerrada y publicada) |
| Contrato rector | `FASE_8_CONTRATO.md` v1.1. En caso de conflicto prevalece ese contrato |
| Documentos relacionados | `FASE_8_2_CONTRATO.md` v1.1 (salida `FragmentoPreparado`), `FASE_6_CONTRATO.md` (proveedor de chat, códigos `AI_*`, 502), `FASE_6X_CONTRATO.md` |

Este documento desarrolla la subfase **8.3** tal como la define `FASE_8_CONTRATO.md` §21:

> **8.3 Embeddings.** Alcance: `IEmbeddingProvider`, proveedor compatible con OpenAI, proveedor simulado, opciones, resiliencia, validación de dimensiones. Exclusiones: indexación. Archivos esperados: interfaz, proveedores, opciones, registro en DI. Migraciones: 0. Pruebas: U e I con transporte simulado (timeout, 429, dimensión errónea, cancelación). PASS: errores tipados; nunca vectores inventados. Depende de: 8.1.

Por la regla de gates de `FASE_8_2_CONTRATO.md` §20, la 8.3 empieza después del cierre de la 8.2 (cumplido en `6e0847b3`).

No se modifica ninguna regla del contrato rector. Donde ese contrato no fija un detalle necesario, se señala en §20 como **decisión pendiente con propuesta**. Las contradicciones encontradas se documentan en §20.1.

### Cambios v1.1 → v1.2 (segunda revisión)

| # | Corrección | Secciones |
|---|---|---|
| 1 | **X4:** se documenta explícitamente que `MaxRetries = 2` es el valor del contrato rector, que la 8.3 lo trata como fijo para preservar exactamente la política de como máximo 3 intentos, y que no hay semántica ni valor nuevos | §8.3, §12, §20 |
| 2 | **X5:** los cambios de `EmbeddingBatchResult` pasan a ser la **adenda explícita A-8.3-1** al contrato rector §9, con sus reglas y la obligación de mantenerla alineada | §6.3, §20 |
| 3 | **D2:** el retardo base de 1,5 s es contractual (patrón del chat, `FASE_6X_CONTRATO.md`: "backoff exponencial (base 1,5 s)") y **deja de ser configurable**. Se elimina `AI:Embeddings:RetryBaseDelaySeconds` | §8.3, §9, §12, §19, §20 |
| 4 | **D1:** el proveedor simulado solo se admite fuera de producción. En `Production` hace falta un proveedor real configurado, o la aplicación no arranca | §12, §19, §20 |
| 5 | §20 rehecho: tabla final X1–X5 con texto del contrato rector, decisión, motivo, impacto y necesidad de modificarlo; D1–D12 con estado (Aprobable, Pendiente, Dependencia de 8.4, Dependencia del contrato rector) | §20 |

### Cambios v1.0 → v1.1 (revisión del contrato)

| # | Corrección | Secciones |
|---|---|---|
| 1 | **D11:** la política de reintentos queda **fija**: como máximo 2 reintentos y 3 intentos en total, solo ante 429, 503 y errores de red. `MaxRetries` deja de ser configurable en la 8.3 (X4) | §8.3, §9, §12, §19, §20 |
| 2 | **D6:** el proveedor ya no estima tokens. Si la respuesta no trae `usage`, el resultado indica "consumo no informado" (`TokensEntrada = null`). Cualquier estimación será una decisión de la 8.4 (X5) | §6, §8.2, §13, §14, §16, §19, §20 |
| 3 | **D9:** 0,02 USD por millón deja de ser un precio contractual. Se conserva solo como referencia de diseño del contrato rector, sin opción de configuración ni constante funcional; la política de costes queda pendiente | §12, §14, §20 |
| 4 | **D12:** se distinguen el modelo solicitado y el declarado por el proveedor. Un modelo declarado distinto del solicitado se **rechaza** (`ModeloInesperado`); el modelo efectivo nunca se sobrescribe en silencio (X5) | §6, §8.2, §9, §15, §19, §20 |
| 5 | Clasificación de errores explícita: `Motivo`, `EsTransitorio` (derivado del motivo, no modificable), `CodigoEstadoHttp`, `Intentos` y `EsperaSugerida`, también para el timeout. La 8.3 no actúa sobre esa clasificación | §9, §19 |

---

## 1. Objetivo

Proveer, detrás de una abstracción de Application, la conversión de **una lista ordenada de textos** en **una lista ordenada de vectores de exactamente 1536 dimensiones**, con:
- errores tipados;
- timeout duro;
- reintentos acotados;
- cancelación;
- validación estricta de la respuesta;
- datos de consumo (tokens, proveedor, modelo) para que **otras fases** los registren.

La 8.3 **no persiste nada, no indexa nada y no tiene consumidores en producción**. Su primer consumidor será la 8.4 (worker de indexación) y, después, la 8.5 (embedding de la consulta).

## 2. Alcance

**Dentro de la 8.3:**
1. `IEmbeddingProvider`, `EmbeddingPurpose` y `EmbeddingBatchResult` (Application), con la forma de `FASE_8_CONTRATO.md` §9.
2. `OpenAICompatibleEmbeddingProvider` (Infrastructure): `POST {BaseUrl}/embeddings`.
3. `MockEmbeddingProvider` (Infrastructure): determinista, sin red.
4. `EmbeddingOptions` (sección `AI:Embeddings`) con validación al arrancar.
5. Resiliencia HTTP: timeout, reintentos, backoff con jitter.
6. Validación de la respuesta: cardinalidad, orden, dimensión, valores finitos.
7. Clasificación de errores para los consumidores (§9), sin códigos públicos nuevos.
8. Registro en DI y selección del proveedor por configuración.
9. Pruebas unitarias y de integración con transporte HTTP simulado.

## 3. Fuera de alcance

| Tema | Subfase |
|---|---|
| Persistir embeddings; escribir `documento_fragmentos.Embedding`; crear o cambiar filas de `documento_indices` | 8.4 |
| Worker de indexación, sembrado, `SKIP LOCKED`, lease, latido, recuperación, reintentos con backoff a nivel de índice, purga, tope diario de tokens | 8.4 |
| Composición del perfil de indexación completo (`…|chunk-v1|ext-v1|norm-v1`) y reindexación | 8.4 |
| Registro en `AIUsageLog` de cualquier llamada (worker o usuario) | 8.4 (worker) y 8.5 (búsqueda) |
| Auditoría `DOCUMENT_INDEX_*` | 8.4 |
| Embedding de la consulta, búsqueda vectorial y léxica, RRF, endpoints, RAG, citas | 8.5 y 8.6 |
| Proveedor autoalojado (`bge-m3`), Docker, GPU, columna de otra dimensión | Fuera de la Fase 8 (§17) |
| Cambios en el proveedor de chat (`IAIProvider`, `OpenAICompatibleProvider`, `AI:OpenAICompatible`) | Ninguna: no se tocan |

## 4. Estado actual verificado (commit `6e0847b3`)

| Elemento | Estado | Evidencia |
|---|---|---|
| `IAIProvider` (chat) con `ProviderId`, `Capabilities`, `CompleteChatAsync`, `StreamChatAsync` y `EstimateTokens` | Existe | `Application/Common/Interfaces/AI/IAIProvider.cs` |
| `OpenAICompatibleProvider`: timeout por petición con CTS enlazado (que incluye los reintentos); traduce fallos a `AIProviderException` / `AIProviderTimeoutException`; logs solo con código de estado o tipo de excepción; la cancelación del llamador se propaga | Existe | `Infrastructure/Services/AI/OpenAICompatibleProvider.cs` |
| `OpenAIOptions` (`AI:OpenAICompatible`): `BaseUrl`, `ApiKey`, `ModelId`, `TimeoutSeconds = 60`, `MaxRetries = 2`, `RetryBaseDelaySeconds = 1.5` | Existe | `OpenAIOptions.cs` |
| Resiliencia: `AddResilienceHandler("ai-provider-retry")` con exponencial y jitter; reintenta **solo** `HttpRequestException`, 429 y 503; `HttpClient.Timeout` infinito | Existe | `DependencyInjection.cs` (líneas 185–213); paquete `Microsoft.Extensions.Http.Resilience` 10.10.0 |
| Selección del proveedor de chat por `AI:Provider` (`Mock` por defecto) | Existe | `DependencyInjection.cs` |
| `AIProviderException` (`AI_PROVIDER_ERROR`) y `AIProviderTimeoutException` (`AI_PROVIDER_TIMEOUT`, deriva de la anterior) → **502** en `GlobalExceptionMiddleware` | Existe | `DomainExceptions.cs`; `GlobalExceptionMiddleware.cs` |
| `TooManyRequestsException` (`TOO_MANY_REQUESTS`, 429) | Existe | Es el rate limit **propio** de la API hacia sus clientes, no el del proveedor externo |
| Coste del chat calculado en `AIService.CalculateCost` con constantes (0,15 / 0,60 USD por millón) | Existe | `AIService.cs` |
| `AIUsageLog` con `Origen` (Usuario, Worker, Sistema), `ActorSistema` y `CHECK`; `AICasoUso.IndexacionSemantica = 6`, `BusquedaSemantica = 7` | Existe (8.1) | `AIUsageLog.cs`, `AICasoUso.cs` |
| `DocumentoIndice.DimensionesPerfilInicial = 1536`; `documento_fragmentos.Embedding vector(1536)`; `CHECK (Dimensiones = 1536)` | Existe (8.1) | `DocumentoIndice.cs`, migración `Fase81IndiceSemantico` |
| `FragmentoPreparado.Texto` como única entrada prevista hacia el proveedor (como mucho 2.000 caracteres) | Existe (8.2) | `Fragmentador.cs`; `FASE_8_2_CONTRATO.md` §9 |
| User-secrets en la API (`UserSecretsId`) | Existe | `AsistenteJuridico.API.csproj` |
| Patrón de pruebas con `StubHttpMessageHandler` y `ConfigurePrimaryHttpMessageHandler` | Existe | `tests/.../Fase6/Fase62TestSupport.cs` |
| `IEmbeddingProvider`, proveedores de embeddings, `AI:Embeddings` | No existe | — |

## 5. Arquitectura

```text
Application                                   Infrastructure
───────────                                   ──────────────
IEmbeddingProvider  ◀── implementa ────────── OpenAICompatibleEmbeddingProvider ── HttpClient tipado
EmbeddingPurpose                                 │                                  + "ai-embeddings-retry"
EmbeddingBatchResult                             └─ EmbeddingOptions (AI:Embeddings)
EmbeddingProviderException (§9)
                    ◀── implementa ────────── MockEmbeddingProvider (sin red)

                     DI: AI:Embeddings:Provider = Mock | OpenAICompatible  →  IEmbeddingProvider
```

- **Dominio:** no cambia.
- **Application:** solo la interfaz, los tipos de resultado y la excepción tipada. No conoce HTTP, OpenAI ni pgvector.
- **Infrastructure:** las dos implementaciones, sus opciones y el registro en DI.
- **Consumidores:** ninguno en la 8.3 (una prueba de arquitectura lo verifica, §18).

## 6. Interfaz

Forma de `FASE_8_CONTRATO.md` §9, con **dos ajustes en `EmbeddingBatchResult`** que piden las correcciones D6 y D12 de la revisión. No cambian la interfaz y quedan documentados como tensión X5 (§20.1):

```csharp
public enum EmbeddingPurpose { Documento, Consulta }

public sealed record EmbeddingBatchResult(
    IReadOnlyList<float[]> Vectores,
    int? TokensEntrada,       // §9 rector: int. v1.1: null = el proveedor no informó el consumo (nunca una estimación)
    string ModelId,           // modelo efectivo (§8.2): el solicitado, confirmado por el proveedor o no declarado por él
    string? ModeloDeclarado,  // v1.1: el campo "model" de la respuesta tal cual, o null si no vino
    string ProviderId,
    int Dimensiones);

public interface IEmbeddingProvider
{
    string ProviderId { get; }
    string ModelId { get; }
    int Dimensiones { get; }
    int MaxTokensPorEntrada { get; }
    int MaxEntradasPorLote { get; }

    Task<EmbeddingBatchResult> EmbedAsync(IReadOnlyList<string> entradas, EmbeddingPurpose proposito, CancellationToken ct);
}
```

### 6.1 Contrato de `EmbedAsync`

| Aspecto | Regla |
|---|---|
| **Entrada** | 1 a `MaxEntradasPorLote` textos, en el orden del llamador. Cada texto es un `FragmentoPreparado.Texto` (8.4) o el texto de una consulta (8.5) |
| **Validación de entrada** (antes de cualquier llamada de red) | `entradas` nulo → `ArgumentNullException`. Lista vacía, más de `MaxEntradasPorLote` elementos, algún elemento nulo, vacío o solo con espacios, UTF-16 mal formado (`TextoNormalizador.EsUtf16BienFormado`) o un texto con `max(1, ceil(longitud / 4)) > MaxTokensPorEntrada` → `ArgumentException`. Son errores de programación del llamador, no del proveedor (D4) |
| **Salida** | `Vectores.Count == entradas.Count`; `Vectores[i]` corresponde a `entradas[i]`; cada vector tiene exactamente `Dimensiones` (1536) valores finitos y norma mayor que 0 |
| **Metadatos** | `ProviderId` y `Dimensiones` son los del proveedor. `ModelId` es el modelo efectivo y `ModeloDeclarado`, el que informa el proveedor (§8.2, D12). `TokensEntrada` es el consumo **informado** por el proveedor, o `null` si no lo informó (§8.2, D6) |
| **Duplicados** | Se permiten. Cada posición recibe su vector; no hay deduplicación en la 8.3 |
| **Errores** | Solo `EmbeddingProviderException` (deriva de `AIProviderException`, código `AI_PROVIDER_ERROR`) o `EmbeddingProviderTimeoutException` (deriva de `AIProviderTimeoutException`, código `AI_PROVIDER_TIMEOUT`); ambas con la clasificación de §9. Nunca se devuelven vectores vacíos, parciales, rellenados ni inventados |
| **Cancelación** | Si el `ct` del llamador se cancela, se propaga `OperationCanceledException` sin traducir, como en el proveedor de chat. El timeout interno **no** es una cancelación: es `EmbeddingProviderTimeoutException` |
| **Atomicidad** | Todo o nada: o devuelve los `n` vectores válidos o lanza una excepción |
| **Efectos** | Ninguno fuera de la llamada HTTP (proveedor externo). No escribe en base de datos ni registra consumo |
| **Concurrencia** | Las implementaciones no tienen estado mutable compartido; se pueden usar desde varios hilos |

### 6.2 Batching

| Aspecto | Regla |
|---|---|
| Tamaño máximo | **64** textos por llamada (`MaxEntradasPorLote`, §3.2.2). Es el máximo también de la configuración |
| Lote vacío | `ArgumentException` sin llamada de red (D4) |
| Partición | **No la hace la 8.3**: el llamador (la 8.4) divide los fragmentos en lotes de como máximo 64. El proveedor rechaza lotes mayores; nunca los divide ni los trunca |
| Orden y correspondencia | 1:1 por posición; la respuesta se reordena por `index` (§8.2) |
| Faltan o sobran vectores | `RespuestaInvalida`; nada devuelto |
| Duplicados | Permitidos; un vector por posición |
| Payload | Como máximo 64 × 8.191 tokens estimados en la petición (en la práctica, 64 × 2.000 caracteres desde la 8.2) y 16 MiB en la respuesta (§11) |
| Una llamada por lote | Sí: `EmbedAsync` hace una sola petición HTTP lógica (más los reintentos de §8.3) |

### 6.3 Adenda A-8.3-1 al contrato rector §9 (`EmbeddingBatchResult`)

**Es una adenda explícita, no un cambio silencioso.** `FASE_8_CONTRATO.md` §9 define `EmbeddingBatchResult(IReadOnlyList<float[]> Vectores, int TokensEntrada, string ModelId, string ProviderId, int Dimensiones)`. Para la 8.3 (y las fases que lo consuman) se aprueba esta forma ampliada:

| Campo | Contrato rector §9 | Adenda A-8.3-1 | Regla |
|---|---|---|---|
| `TokensEntrada` | `int` | **`int?`** | `null` = **consumo no informado** por el proveedor. `0` conserva su significado numérico (el proveedor informó cero). La 8.3 **nunca estima**; una estimación, si hace falta, la define la 8.4 y la marca como tal |
| `ModeloDeclarado` | — (no existe) | **`string?`** (nuevo) | El campo `model` de la respuesta tal cual; `null` si no vino, vino `null` o vacío. Si viene y **difiere** del modelo solicitado, la respuesta se **rechaza** con `ModeloInesperado` (§8.2) y no existe resultado |
| `ModelId` | `string` | `string` (sin cambio de tipo) | **Modelo efectivo** (§8.2): el solicitado, que es igual al declarado o no fue declarado. La firma (§15) usa este modelo |
| `Vectores`, `ProviderId`, `Dimensiones` | Sin cambios | Sin cambios | — |

- `IEmbeddingProvider` (la interfaz) **no cambia**: la adenda solo afecta al tipo de resultado.
- **No se modifica `docs/FASE_8_CONTRATO.md`.** La adenda deberá **mantenerse alineada con el contrato rector**: si este se actualiza (por ejemplo, en el cierre 8.7, que actualiza el contrato con los resultados), §9 deberá incorporar la forma de la adenda. Cualquier cambio futuro de §9 debe revisar la adenda.

`EmbeddingPurpose` existe para los modelos que exigen prefijos (`query:` y `passage:` en E5 o BGE). **El proveedor compatible con OpenAI y el simulado lo ignoran** (§17).

## 7. Modelo de embedding y dimensión

| Aspecto | Valor | Fuente |
|---|---|---|
| Modelo inicial | `text-embedding-3-small` | §3.2 |
| Dimensión | **1536**, igual a `DocumentoIndice.DimensionesPerfilInicial` y a la columna `vector(1536)` | §3.2.1 |
| Métrica prevista | Distancia coseno (`<=>`) en pgvector; similitud `1 − distancia` | §10, DA8-19 |
| Entrada máxima | 8.191 tokens por texto (`MaxTokensPorEntrada`) | §3.2 |
| Parámetro `dimensions` | **No se envía.** El modelo devuelve 1536 por defecto; enviarlo sería una reducción de dimensión, que solo se permite si un perfil la declara de forma explícita (§3.2.1). No hay ningún perfil así en la Fase 8 | §3.2.1 |

**Prohibido** (§3.2.1): padding, truncamiento, redimensionamiento, normalización que cambie la dimensión, aceptar otra dimensión o convertir en silencio un vector incompatible.

**Dimensión distinta en la respuesta:**
- error explícito: `EmbeddingProviderException` con `Motivo = DimensionInvalida` (permanente);
- log de error con la dimensión esperada y la recibida (solo números);
- ningún vector del lote se devuelve.

La trazabilidad persistente (`DOCUMENT_INDEX_FAILED` con su código) corresponde a la 8.4, que es quien conoce el documento. La 8.3 no audita porque no tiene documento ni tenant. La columna `vector(1536)` de PostgreSQL sigue siendo la última barrera (§3.2.1, capa 3).

**Arranque:** `AI:Embeddings:Dimensiones` debe ser exactamente 1536. Cualquier otro valor impide arrancar la aplicación. Otra dimensión exige primero la migración de columna tipada de §3.2.1.

## 8. Proveedor compatible con OpenAI

### 8.1 Petición

- `POST {BaseUrl}/embeddings`, con `Content-Type: application/json`.
- Cuerpo **exacto** (y nada más):

  ```json
  { "model": "<ModelId>", "input": ["<texto 1>", "…", "<texto n>"], "encoding_format": "float" }
  ```

  - No se envía `user`, `dimensions` ni ningún otro campo.
  - No se envían identificadores de tenant, documento, expediente ni usuario, ni títulos, rutas o hashes (§10).
- Cabeceras: solo `Authorization: Bearer <ApiKey>` (si hay clave) y las estándar de `HttpClient`. Nunca se reenvían cabeceras, cookies ni JWT de una petición de usuario.

### 8.2 Respuesta y validación

Formato esperado: `{ "data": [ { "index": i, "embedding": [f, …] }, … ], "model": "…", "usage": { "prompt_tokens": t, … } }`.

Validaciones, todas obligatorias. Cualquier fallo es `RespuestaInvalida`, `DimensionInvalida` o `ModeloInesperado` (todos permanentes), y no se devuelve nada:

| Validación | Fallo |
|---|---|
| Cuerpo JSON válido con `data` como array | JSON inválido o sin `data` |
| `data.Count == entradas.Count` | Faltan vectores (respuesta parcial) o sobran |
| Los `index` forman exactamente la permutación `0…n−1` (sin duplicados ni huecos ni valores fuera de rango); los vectores se **reordenan por `index`** | Índice ausente, repetido o fuera de rango |
| Cada `embedding` es un array numérico de exactamente `Dimensiones` elementos | `DimensionInvalida` |
| Todos los valores son finitos al convertir a `float` (sin NaN ni ±Infinito) y la norma del vector es mayor que 0 | Vector inválido (D5) |
| Tamaño de la respuesta ≤ 16 MiB | Respuesta demasiado grande (D7) |

**Modelo (D12).** Hay dos modelos distintos y nunca se confunden:

| Concepto | Valor |
|---|---|
| **Modelo solicitado** | `AI:Embeddings:ModelId`, enviado en el campo `model` de la petición |
| **Modelo declarado** | El campo `model` de la respuesta, si existe y es una cadena no vacía. Se devuelve tal cual en `ModeloDeclarado` |

| Caso | Resultado |
|---|---|
| Declarado **igual** al solicitado (comparación ordinal exacta) | Aceptado. Modelo efectivo = solicitado = declarado |
| Declarado **ausente** (sin campo, `null` o cadena vacía) | Aceptado. Modelo efectivo = solicitado; `ModeloDeclarado = null`, así el consumidor sabe que el proveedor no lo confirmó |
| Declarado **distinto** del solicitado (otro modelo, alias, versión con sufijo o diferencia de mayúsculas) | **Rechazado**: `EmbeddingProviderException(ModeloInesperado)`, permanente; ningún vector se devuelve |

Así `ModelId` nunca se sobrescribe en silencio: o coincide con lo que declaró el proveedor o el proveedor no declaró nada. La firma (§15) usa siempre ese modelo efectivo. Si un servidor compatible informa otro nombre para el mismo modelo, la corrección es configurar `ModelId` con ese nombre, lo que da un perfil nuevo, en vez de aceptar respuestas incoherentes.

**Tokens (D6).** El proveedor **nunca estima** el consumo:
- si `usage.prompt_tokens` viene en la respuesta como entero ≥ 0, `TokensEntrada = usage.prompt_tokens`, el consumo informado;
- si falta `usage` o `prompt_tokens`, o no es un entero ≥ 0, `TokensEntrada = null` (consumo no informado) y se registra un log de advertencia sin contenido. No es un error: los vectores son válidos;
- cómo registra la 8.4 un consumo no informado (estimación marcada como tal, tokens 0 o lo que su contrato decida) es una **decisión futura de la 8.4**. La 8.3 no la anticipa.

### 8.3 Timeout, reintentos y backoff

Valores de `FASE_8_CONTRATO.md` §3.2.2 y §9, con el mismo patrón que el proveedor de chat:

| Aspecto | Valor |
|---|---|
| Timeout | **60 s por lote, límite duro que incluye los reintentos y sus esperas**. Se implementa con un CTS enlazado al `ct` del llamador, como `OpenAICompatibleProvider`; `HttpClient.Timeout` es infinito |
| Reintentos | **`MaxRetries = 2`, el valor del contrato rector (§3.2.2 y §9), tratado como fijo (D11, X4):** como máximo 2 reintentos, es decir, como máximo 3 intentos en total. La restricción existe solo para preservar exactamente esa política contractual; no introduce semántica ni valor nuevos. No hay ninguna opción que permita otro número de intentos (en particular, nunca 5). Se aplica en un manejador de resiliencia propio (`ai-embeddings-retry`) con la constante `MaxReintentos = 2` |
| Backoff | Exponencial con jitter, **retardo base de 1,5 s, constante contractual y no configurable** (D2): el contrato rector §3.2.2 exige "el mismo patrón que el proveedor de chat", que `FASE_6X_CONTRATO.md` fija como "backoff exponencial (base 1,5 s) y jitter". Respeta `Retry-After` en 429, siempre dentro del límite de 60 s |
| Reintentables (dentro de la llamada) | `HttpRequestException` (red, DNS, conexión), **429** y **503**: exactamente el conjunto del chat (§3.2.2: "429, 503 y errores de red") |
| No reintentables (dentro de la llamada) | 400, 401, 403, 404, 409, 413 y 422 (cualquier otro 4xx); 500, 502 y 504; respuestas 2xx inválidas |

Que 500, 502 y 504 no se reintenten **dentro** de la llamada no los convierte en permanentes. Se clasifican como transitorios (§9) para que la 8.4 los reintente con su propio backoff, como dice §7 del contrato rector ("5xx, red, 429: transitorio").

### 8.4 Cancelación y fallos

| Situación | Resultado |
|---|---|
| `ct` del llamador cancelado (antes o durante la llamada, o durante una espera entre reintentos) | `OperationCanceledException` propagada |
| Vence el límite de 60 s | `EmbeddingProviderTimeoutException(Timeout)`, transitorio. Log de advertencia con el timeout |
| `model` declarado distinto del solicitado | `EmbeddingProviderException(ModeloInesperado)`, permanente |
| 429 tras agotar los reintentos | `EmbeddingProviderException(LimiteDeTasa)`, transitorio |
| 503 o error de red tras agotar los reintentos | `EmbeddingProviderException(NoDisponible)`, transitorio |
| 500, 502 o 504 | `EmbeddingProviderException(ErrorDelServidor)`, transitorio |
| 401 o 403 | `EmbeddingProviderException(Autenticacion)`, permanente |
| Otros 4xx (400, 404, 413, 422…) | `EmbeddingProviderException(SolicitudRechazada)`, permanente |
| Respuesta 2xx inválida (§8.2) | `RespuestaInvalida` o `DimensionInvalida`, permanentes |

En todos los casos el log incluye **solo** el código de estado HTTP o el tipo de excepción, y la excepción lleva un mensaje fijo. El cuerpo de la respuesta del proveedor nunca se registra ni se incluye en la excepción, porque puede reflejar el texto enviado.

## 9. Errores

**No se crean códigos públicos nuevos.** `FASE_8_CONTRATO.md` §9 fija que el proveedor "lanza `AIProviderException` / `AIProviderTimeoutException` (códigos `AI_*` existentes)". Para la 8.5 eso significa 502 (`AI_PROVIDER_ERROR` / `AI_PROVIDER_TIMEOUT`), como §3.2.2.

La 8.4 necesita distinguir lo transitorio de lo permanente. Para eso se proponen dos excepciones tipadas que **conservan los códigos públicos** y comparten una clasificación explícita (D3):

```csharp
public enum MotivoFalloEmbedding
{
    // Transitorios
    Timeout, LimiteDeTasa, NoDisponible, ErrorDelServidor,
    // Permanentes
    Autenticacion, SolicitudRechazada, RespuestaInvalida, DimensionInvalida, ModeloInesperado
}

/// Clasificación común a todo fallo del proveedor de embeddings.
public interface IFalloProveedorEmbeddings
{
    MotivoFalloEmbedding Motivo { get; }
    bool EsTransitorio { get; }        // derivado SOLO de Motivo (tabla fija de abajo); no se puede asignar
    int? CodigoEstadoHttp { get; }     // último código HTTP recibido; null si no hubo respuesta (red, timeout) o si el fallo es de validación de un 2xx
    int Intentos { get; }              // intentos HTTP efectuados en la llamada: 1 a 3
    TimeSpan? EsperaSugerida { get; }  // Retry-After del último 429 o 503, si vino y es válido; null en otro caso
}

public sealed class EmbeddingProviderException : AIProviderException, IFalloProveedorEmbeddings { … }               // AI_PROVIDER_ERROR
public sealed class EmbeddingProviderTimeoutException : AIProviderTimeoutException, IFalloProveedorEmbeddings { … }  // AI_PROVIDER_TIMEOUT, Motivo = Timeout
```

**Tabla fija Motivo → EsTransitorio:**

| Transitorio | Permanente |
|---|---|
| `Timeout`, `LimiteDeTasa`, `NoDisponible`, `ErrorDelServidor` | `Autenticacion`, `SolicitudRechazada`, `RespuestaInvalida`, `DimensionInvalida`, `ModeloInesperado` |

**Reglas de la clasificación:**
- Los mensajes de las excepciones son fijos y no contienen la clave, los textos, el cuerpo de la respuesta, la URL ni los vectores. `ToString()` tampoco, y no hay excepción interna con datos del proveedor.
- La 8.3 **solo informa**. No reintenta más allá de §8.3, no aplaza, no reindexa, no toca `DocumentoIndice` ni `DocumentoFragmento`, no marca fragmentos, no adquiere leases, no ejecuta workers ni recuperaciones. Qué hace la 8.4 con cada motivo (por ejemplo, transitorio → `Pendiente` con backoff; permanente → `Fallido`) lo decidirá su contrato.
- Los consumidores que solo conocen `AIProviderException` (por ejemplo, el middleware → 502) siguen funcionando sin cambios.

**Catálogo de la 8.3:**

| Categoría | Cuándo | Tipo | Código público | ¿Transitorio? |
|---|---|---|---|---|
| Configuración inválida | `BaseUrl` no absoluta, esquema no permitido, `Dimensiones ≠ 1536`, `MaxEntradasPorLote` fuera de 1–64, `TimeoutSeconds ≤ 0`, proveedor desconocido, proveedor `Mock` (explícito o por defecto) en el entorno `Production` (D1) | `OptionsValidationException` **al arrancar**: la aplicación no inicia | — | — |
| Credencial ausente | Proveedor `OpenAICompatible` sin `ApiKey` (salvo host de loopback, D8) | Igual: no inicia | — | — |
| Lote inválido | Lista vacía, más de 64, elemento nulo o vacío, UTF-16 mal formado, texto demasiado largo | `ArgumentException` / `ArgumentNullException` (programación) | — | — |
| Cancelación | `ct` del llamador | `OperationCanceledException` | — | — |
| Timeout | Límite de 60 s | `EmbeddingProviderTimeoutException(Timeout)` | `AI_PROVIDER_TIMEOUT` | Sí |
| Rate limit del proveedor | 429 tras los reintentos | `EmbeddingProviderException(LimiteDeTasa)` | `AI_PROVIDER_ERROR` | Sí |
| Proveedor no disponible | 503 o red tras los reintentos | `…(NoDisponible)` | `AI_PROVIDER_ERROR` | Sí |
| Error transitorio del servidor | 500, 502, 504 | `…(ErrorDelServidor)` | `AI_PROVIDER_ERROR` | Sí |
| Error permanente: credencial rechazada | 401, 403 | `…(Autenticacion)` | `AI_PROVIDER_ERROR` | No |
| Error permanente: petición rechazada | Otros 4xx | `…(SolicitudRechazada)` | `AI_PROVIDER_ERROR` | No |
| Respuesta inválida | JSON, cardinalidad, índices, NaN/∞, norma 0, tamaño | `…(RespuestaInvalida)` | `AI_PROVIDER_ERROR` | No |
| Dimensión inválida | Vector de otra longitud | `…(DimensionInvalida)` | `AI_PROVIDER_ERROR` | No |
| Modelo inesperado | `model` declarado distinto del solicitado | `…(ModeloInesperado)` | `AI_PROVIDER_ERROR` | No |

`TOO_MANY_REQUESTS` **no** se usa: es el rate limit propio de la API hacia sus clientes (Fase 6), no el del proveedor externo.

## 10. Privacidad

De `FASE_8_CONTRATO.md` §3.2.2 y §13.

**Entra al proveedor ÚNICAMENTE** el array `input` con los textos que recibe `EmbedAsync`:
- en la 8.4, `FragmentoPreparado.Texto`, que es contenido normalizado del documento más, en su caso, el prefijo `Sección: …` o la cabecera de hoja definidos en `FASE_8_2_CONTRATO.md` §8.6 y §8.7, que también son contenido;
- en la 8.5, el texto de la consulta;
- en lotes de como máximo 64.

**La 8.3 no decide qué tenant ni qué documento se envía.** Lo garantiza el llamador: la 8.4, con el tenant del documento, la indexación activa y el documento activo. El proveedor no recibe ni conoce identificadores, así que no puede mezclar tenants. Cada llamada es independiente y sin estado.

**Nunca se envía:** documentos completos (solo fragmentos, de como máximo 2.000 caracteres); datos de otros tenants; identificadores internos (tenant, documento, expediente, usuario, índice, fragmento); títulos; rutas; hashes; JWT; cookies; cabeceras del usuario; credenciales propias (salvo la clave del proveedor en `Authorization`); información de infraestructura. Tampoco el campo `user` de la API de OpenAI.

**El contenido no se anonimiza:** los nombres de las partes forman parte del texto, como decidió la 8.2 (D3 de la 8.2).

## 11. Seguridad

| Medida | Regla |
|---|---|
| **SSRF** | `BaseUrl` solo procede de la configuración del despliegue, nunca de una petición de usuario. Validación al arrancar: URI absoluta, sin credenciales en la URL (`userinfo`), sin query ni fragmento |
| **HTTPS** | Obligatorio. `http` solo se admite con hosts de loopback (`localhost`, `127.0.0.1`, `::1`), para servidores locales compatibles en desarrollo (TEI, vLLM, Ollama) (D7) |
| **Redirecciones** | `AllowAutoRedirect = false`: una redirección se trata como `SolicitudRechazada` (3xx inesperado) y la clave nunca viaja a otro host |
| **Límites de payload** | Petición: como máximo 64 textos y 8.191 tokens estimados por texto (en la práctica, como máximo 2.000 caracteres desde la 8.2). Respuesta: como máximo 16 MiB, más que suficiente para 64 × 1536 valores (D7) |
| **Secretos** | `ApiKey` solo desde user-secrets (desarrollo) o variables de entorno o almacén de secretos (despliegue). Nunca en `appsettings*.json` versionados, en la base de datos, en logs, en mensajes de excepción, en mensajes de validación de opciones ni en pruebas (las pruebas usan valores ficticios, por ejemplo `clave-de-prueba`) |
| **Cancelación** | El `ct` del llamador llega hasta `HttpClient.SendAsync` y a la espera entre reintentos |
| **Aislamiento** | Proveedor sin estado y sin caché de vectores ni de textos |

## 12. Configuración

Sección propia `AI:Embeddings`, fijada por `FASE_8_CONTRATO.md` §3.2.2 y §9. **No se modifica `AI:OpenAICompatible`** (chat) ni se reutilizan sus valores: los embeddings pueden apuntar a otro servidor o modelo que el chat (ver X1 en §20.1).

| Clave | Por defecto | Validación | Tipo | Origen |
|---|---|---|---|---|
| `AI:Embeddings:Provider` | `Mock` **solo fuera de `Production`** | `Mock` u `OpenAICompatible`. En `Production`, `Mock` (explícito o por defecto) impide arrancar (D1) | No secreta | **D1** (la clave no está en el contrato rector; §9 limita el simulado a "pruebas y desarrollo") |
| `AI:Embeddings:BaseUrl` | `https://api.openai.com/v1` | §11 | No secreta | §9 |
| `AI:Embeddings:ApiKey` | (vacía) | Obligatoria con `OpenAICompatible` salvo loopback (D8) | **Secreta**: user-secrets o entorno | §3.2.2 |
| `AI:Embeddings:ModelId` | `text-embedding-3-small` | No vacía | No secreta | §3.2 |
| `AI:Embeddings:Dimensiones` | 1536 | = 1536 | No secreta | §3.2.1 |
| `AI:Embeddings:TimeoutSeconds` | 60 | > 0 | No secreta | §3.2.2 |
| `AI:Embeddings:MaxEntradasPorLote` | 64 | 1–64 | No secreta | §3.2.2 |

**Lo que NO es configuración** en la 8.3:
- **Reintentos:** `MaxRetries = 2` del contrato rector, como constante `MaxReintentos = 2`: como máximo 3 intentos en total (D11, X4). No existe la clave `AI:Embeddings:MaxRetries`; si apareciera en la configuración, se ignoraría, y una prueba lo verifica.
- **Retardo base del backoff:** constante de 1,5 s (D2). No existe `AI:Embeddings:RetryBaseDelaySeconds`.
- **Proveedor en producción (D1):** la regla se valida al arrancar (`ValidateOnStart` con `IHostEnvironment`), el mismo mecanismo que ya usa la validación del lease de la 6.X en `DependencyInjection.cs`; no se añade otro. En `Development` y en los hosts de prueba se admite `Mock`.
  - **Impacto:** desde la 8.3, un despliegue `Production` debe configurar `AI:Embeddings:Provider = OpenAICompatible` y su clave aunque todavía no haya consumidores (§20.2, D1).
  - El proveedor de **chat** (`AI:Provider`, también `Mock` por defecto) no tiene hoy esa protección. Se anota como observación y **no se cambia en la 8.3**.
- **Coste o precio por token:** no hay opción ni constante funcional (D9, §14).
- `MaxTokensPorEntrada` (8.191) es una propiedad del modelo: una constante del proveedor.
- La validación se hace con `ValidateOnStart`, y las reglas específicas de `OpenAICompatible` solo se aplican cuando ese es el proveedor seleccionado.
- `appsettings.json` puede incluir solo valores no secretos. En la 8.3 **no hace falta** añadir nada: los valores por defecto viven en la clase de opciones.

## 13. Observabilidad

| Permitido en logs | Prohibido en logs |
|---|---|
| `ProviderId`, modelo solicitado y declarado, número de textos del lote, duración, éxito o fallo, `Motivo` o tipo de excepción, código de estado HTTP, número de intentos, tokens informados (o "no informado"), dimensión esperada y recibida | Texto de las entradas (fragmentos o consultas), vectores (completos o parciales), cuerpo de la respuesta del proveedor, `ApiKey`, cabecera `Authorization`, JWT, cookies, secretos, URL con credenciales, identificadores de tenant, documento o usuario (la 8.3 no los conoce) |

**Métricas** (definidas, no implementadas): lotes por resultado (éxito o motivo), latencia por lote (p50, p95), reintentos por lote, tokens por lote, tamaño del lote. Las podrá implementar la 8.4 o una fase de observabilidad.

**Datos disponibles para un futuro registro en `AIUsageLog`** (§14): `EmbeddingBatchResult` aporta `TokensEntrada` (informado o `null`), `ProviderId`, `ModelId` y `ModeloDeclarado`. Un fallo aporta la clasificación de §9. El llamador mide `DuracionMs` y conoce `Exitoso`.

## 14. `AIUsageLog` y costes

**La 8.3 no escribe en `AIUsageLog`.** Igual que el proveedor de chat (el registro lo hace `AIService`), el proveedor de embeddings solo devuelve los datos de consumo. Quién registra y con qué actor lo fija el contrato rector (§14 y §14.1), sin cambios:

| Llamada | Fase | `Origen` | `UsuarioId` | `ActorSistema` | `CasoUso` |
|---|---|---|---|---|---|
| Indexación de fragmentos (worker) | 8.4 | `Worker` | NULL | `worker:indexacion-semantica` | `IndexacionSemantica` (6) |
| Embedding de la consulta en `buscar` o `preguntar` (interactiva) | 8.5 y 8.6 | `Usuario` | **Obligatorio** (JWT) | NULL | `BusquedaSemantica` (7) |
| Sistema | — | `Sistema` | NULL | `system:<operacion>` | **Ninguna en la Fase 8** (§14.1) |

**Consumo informado frente a estimado (D6):** la 8.3 solo transmite lo que el proveedor informa. Con `TokensEntrada = null` no hay ninguna cifra que la 8.3 presente como real. Si la 8.4 necesita una estimación para registrar o para el tope diario de §14 del contrato rector, la definirá su contrato y la marcará como estimación. **Decisión futura de la 8.4.**

**Intentos fallidos con consumo:** según §14, la 8.4 los registra con `Exitoso = false`. Un fallo de la 8.3 no devuelve tokens (no hay resultado), así que el consumo de un intento fallido no es medible y **no se inventa**. Cómo lo registra la 8.4 lo decidirá su contrato, sin cambiar el modelo de la 8.1.

**Coste (D9) — sin precio contractual en la 8.3:**
- La 8.3 **no calcula costes** y no define ninguna opción ni constante de precio.
- **Referencia de diseño** (solo la que cita el contrato rector §3.2): unos 0,02 USD por millón de tokens de entrada para el proveedor OpenAI y el modelo `text-embedding-3-small`.
  - El 2026-10-04 solo se encontró en fuentes de terceros (agregadores de precios), **no en una fuente oficial de OpenAI**.
  - No es una autoridad válida: **debe verificarse en la documentación oficial del proveedor antes de producción**.
  - La verificación que §3.2 del contrato rector asignaba a la 8.3 queda **incompleta** y se traslada a la fase que defina la política de costes.
- La política definitiva (dónde vive el precio, quién calcula `CostoEstimadoUsd` y cómo se actualiza) queda **pendiente** de la fase que registre el consumo de embeddings (8.4 para la indexación; 8.5 para la búsqueda).

## 15. Versionado del embedding

- El proveedor expone `ProviderId`, `ModelId` (modelo solicitado) y `Dimensiones`, de donde sale la parte de embedding del perfil de `FASE_8_CONTRATO.md` §3.4: `"{proveedor}:{modelo}@{dimensiones}"`.
- **Modelo efectivo de la firma:** el solicitado. Es correcto porque §8.2 garantiza que todo embedding devuelto procede de una respuesta cuyo modelo declarado es **igual** al solicitado o **no fue declarado**. Un modelo declarado distinto se rechaza, así que nunca existe un vector cuyo modelo real conocido difiera de la firma.
  - Para auditar la confirmación, la 8.4 puede guardar o registrar `ModeloDeclarado` si su contrato lo decide.
- Valores:
  - `OpenAICompatibleEmbeddingProvider`: `ProviderId = "openai-compatible"` (el mismo identificador que el chat), con el `ModelId` y las `Dimensiones` configurados. Inicial: `openai-compatible:text-embedding-3-small@1536`.
  - `MockEmbeddingProvider`: `ProviderId = "mock"`, `ModelId = "mock-bow-sha256-v1"`, 1536. Así **los vectores simulados nunca comparten perfil con los reales**.
- Helper sin estado `FirmaEmbedding.De(IEmbeddingProvider) → "{ProviderId}:{ModelId}@{Dimensiones}"` en Application, **sin añadir miembros a la interfaz** del contrato rector (D10). La 8.4 compondrá el perfil completo `…|chunk-v1|ext-v1|norm-v1` con las constantes de la 8.2.
- **Cambio de modelo, de dimensión o de algoritmo del simulado** → firma distinta → perfil distinto. La 8.4 no mezcla perfiles (§3.4). La 8.3 no reindexa ni compara perfiles.

## 16. Proveedor simulado (`MockEmbeddingProvider`)

Algoritmo `mock-bow-sha256-v1` (bolsa de palabras con hashing, §9 del contrato rector), **sin red**:

1. Validación de entrada idéntica a §6.1.
2. **Tokens:** secuencias máximas de *runes* Unicode que son letra o dígito (`Rune.IsLetterOrDigit`), en minúsculas con `Rune.ToLowerInvariant`, sin normalización adicional.
   - Si un texto no tiene ninguna (solo emojis o signos), cada *rune* que no sea espacio en blanco es un token. Así todo texto válido produce al menos un token.
3. Para cada token: `h = SHA-256(UTF-8(token))`; `posición = (UInt64 little-endian de h[0..8]) mod 1536`; `signo = +1` si `h[8]` es par y `−1` si es impar; `v[posición] += signo`.
4. **L2:** `v[i] = (float)(v[i] / ‖v‖)`, calculado en `double` y en orden de índice.
   - Si `‖v‖ = 0` (cancelación exacta de signos), `v[posición del primer token] = 1` antes de normalizar. Así nunca hay vectores de norma 0.
5. **Consumo:** el simulado es él mismo el proveedor, así que su consumo informado es su propia cuenta determinista: `TokensEntrada = Σ max(1, ceil(longitud / 4))`. No estima el consumo de otro proveedor, y la firma `mock:…` impide confundirlo con consumo real de OpenAI. `ModeloDeclarado = "mock-bow-sha256-v1"`. `EmbeddingPurpose` se ignora, de modo que consulta y documento son comparables.
6. Comprueba `ct` antes de empezar y entre textos. Si se cancela, lanza `OperationCanceledException` y no devuelve nada.

**Propiedades:**
- determinista, estable y reproducible: SHA-256 y aritmética IEEE en orden fijo;
- independiente de la cultura (`ToLowerInvariant` sobre *runes*) y de la máquina;
- sin aleatoriedad;
- exactamente 1536 dimensiones;
- los textos con palabras en común tienen similitud coseno mayor que los que no las comparten.

**Simulación de fallos:** el proveedor simulado de producción **no tiene ganchos de fallo**, para no dejar puntos de inyección en el código desplegado. Los errores, timeouts, reintentos, la dimensión inválida y la cardinalidad incorrecta se prueban así (X2 en §20.1):
- con `OpenAICompatibleEmbeddingProvider` sobre un `HttpMessageHandler` simulado, el patrón `StubHttpMessageHandler` de la Fase 6;
- con un doble de prueba programable (`ProveedorEmbeddingsProgramable`) en el proyecto de pruebas, para los consumidores de la 8.4.

## 17. Compatibilidad futura (`bge-m3` y autoalojados)

- `IEmbeddingProvider` no expone nada de OpenAI. Un proveedor autoalojado es **otra implementación**, o la misma `OpenAICompatibleEmbeddingProvider` apuntando a un servidor compatible (TEI o vLLM sirven `/v1/embeddings`), sin tocar el dominio ni Application.
- `EmbeddingPurpose` permite añadir, en esa implementación futura, los prefijos `query:` o `passage:` por configuración del proveedor.
- **`bge-m3` tiene 1024 dimensiones:** la validación al arrancar (`Dimensiones = 1536`) lo impide hasta que exista la migración de columna tipada y el perfil nuevo de §3.2.1. Esa es la protección buscada.
- La 8.3 **no** añade Docker, modelo local, GPU, opciones de prefijos ni otra dimensión.

## 18. Rendimiento (método definido; benchmarks no implementados)

Se medirá en una fase posterior, **separado** del benchmark de búsqueda (§19.1 del contrato rector) y del de RAG:

| Medida | Método |
|---|---|
| Sobrecarga local por lote (serialización, validación, parseo de 64 × 1536 valores) | Transporte simulado con respuesta fija; lotes de 1, 16 y 64; 500 mediciones tras 50 de calentamiento; p50 y p95 |
| Latencia del proveedor externo | Observacional, sin objetivo (depende de un tercero): p50 y p95 por tamaño de lote, con modelo, región y fecha declarados |
| Throughput | Textos por segundo con lotes de 64 y concurrencia 2 (el `MaxParalelismo` de la 8.4) |
| Tamaño medio | Caracteres y tokens medios por texto del corpus medido |

## 19. Pruebas y criterios

### 19.1 Matriz de pruebas

**U** = unitaria; **I** = integración con `HttpClient` real, `AddHttpClient` y el manejador de resiliencia sobre un transporte simulado (sin red); **A** = arquitectura.

| # | Área | Caso | Tipo | Esperado |
|---|---|---|---|---|
| E1 | Contrato | Lote de 1, 2 y 64 textos | U e I | `Vectores.Count = n`; cada vector de 1536 valores finitos con norma > 0 |
| E2 | | Orden: respuesta con `index` desordenado | I | Vectores reordenados: `Vectores[i]` corresponde a `entradas[i]` |
| E3 | | Duplicados en la entrada | U e I | Una posición por entrada; vectores iguales en el simulado |
| E4 | | Lista vacía, 65 textos, elemento nulo, vacío o solo espacios, UTF-16 mal formado, texto de más de 8.191 tokens estimados | U | `ArgumentException` o `ArgumentNullException`; **0 llamadas** al transporte |
| E5 | Simulado | Determinismo: dos ejecuciones e instancias distintas | U | Vectores idénticos bit a bit |
| E6 | | Culturas `es-EC`, `en-US`, `tr-TR` (incluido "İ ı") | U | Vectores idénticos |
| E7 | | Unicode, tildes, emojis, texto solo de signos, texto de 2.000 caracteres | U | 1536 dimensiones, norma 1 ± 1e-6, sin excepción |
| E8 | | Similitud: textos con palabras en común frente a textos sin ellas | U | coseno(común) > coseno(sin común) |
| E9 | | Vectores golden de 3 textos fijos (posición y valor de las primeras componentes no nulas) | U | Iguales a los valores fijados |
| E10 | | Cancelación antes y durante el lote | U | `OperationCanceledException`; sin resultado |
| E11 | OpenAI | 200 válido: cuerpo exacto `{model, input, encoding_format}`, URL `{BaseUrl}/embeddings`, `Authorization: Bearer` | I | Igual; `TokensEntrada = usage.prompt_tokens`; `ModeloDeclarado` igual al de la respuesta |
| E12 | | 200 sin `usage`, con `usage` sin `prompt_tokens` y con `prompt_tokens` negativo o no entero | I | Vectores válidos; **`TokensEntrada = null`** (nunca una estimación); log de advertencia sin contenido |
| E12b | | Modelo declarado igual, ausente (`null`, vacío, sin campo) o distinto (otro modelo, sufijo de versión, otras mayúsculas) | I | Igual o ausente → aceptado con `ModelId` = solicitado y `ModeloDeclarado` = el recibido o `null`; distinto → `ModeloInesperado`, permanente, nada devuelto |
| E13 | | 429 ×1 y después 200 | I | Éxito con 1 reintento |
| E14 | | 429 ×3 (y ×10) | I | `EmbeddingProviderException(LimiteDeTasa)`, transitorio; **exactamente 3 intentos** en ambos casos; `Intentos = 3`; `CodigoEstadoHttp = 429`; `EsperaSugerida` = el `Retry-After` recibido |
| E15 | | 503 ×3; `HttpRequestException` ×3 | I | `NoDisponible`, transitorio; 3 intentos |
| E16 | | 500, 502, 504 | I | `ErrorDelServidor`, transitorio; **1 intento** |
| E17 | | 400, 404, 413, 422 / 401, 403 | I | `SolicitudRechazada` / `Autenticacion`, permanentes; **1 intento** |
| E18 | | Redirección 307 | I | `SolicitudRechazada`; no se sigue |
| E19 | | JSON inválido; sin `data`; `data` con n − 1 o n + 1; índice repetido o fuera de rango | I | `RespuestaInvalida`; nada devuelto |
| E20 | | Vector de 1535 o 1537; NaN; norma 0 | I | `DimensionInvalida` / `RespuestaInvalida`; nada devuelto; sin padding ni truncamiento |
| E21 | | Respuesta de más de 16 MiB | I | `RespuestaInvalida` |
| E22 | | Transporte que no responde (timeout de prueba corto) | I | `EmbeddingProviderTimeoutException` (`AI_PROVIDER_TIMEOUT`, `Motivo = Timeout`, transitorio, `CodigoEstadoHttp = null`); duración < timeout + 1 s; sin peticiones posteriores |
| E23 | | Timeout durante la espera entre reintentos | I | `EmbeddingProviderTimeoutException`; `Intentos` = los efectuados hasta ese momento |
| E24 | | Cancelación del llamador durante la petición y durante la espera de backoff | I | `OperationCanceledException` sin traducir; sin reintentos posteriores |
| E25 | Retry | Esperas entre intentos con un `TimeProvider` falso | I | Crecientes (exponencial) con jitter acotado; respeta `Retry-After` |
| E25b | | Configuración con `AI:Embeddings:MaxRetries = 5` y `AI:Embeddings:RetryBaseDelaySeconds = 30` (claves inexistentes) ante 503 ×10 | I | Se ignoran: **exactamente 3 intentos** y retardo base de 1,5 s (con un `TimeProvider` falso). No existe ninguna propiedad de opciones para los reintentos ni para el retardo (comprobado por reflexión) |
| E25c | Clasificación | Para cada `MotivoFalloEmbedding`: `EsTransitorio` según la tabla fija de §9; código público (`AI_PROVIDER_ERROR` / `AI_PROVIDER_TIMEOUT`); ambas excepciones son `AIProviderException` e `IFalloProveedorEmbeddings` | U | Exacto; `EsTransitorio` no tiene setter |
| E26 | Seguridad | Logger de captura en todos los casos E11–E24 con la clave `clave-secreta-de-prueba` y textos con marcador `SECRETO-FRAGMENTO` | I | Ningún log contiene la clave, `Bearer`, los textos ni números del vector |
| E27 | | Mensaje y `ToString()` de toda excepción lanzada | I | Sin clave, sin textos, sin cuerpo de la respuesta, sin URL con credenciales |
| E28 | | Captura del transporte | I | Solo `model`, `input` (exactamente las entradas, en orden) y `encoding_format`; sin `user` ni `dimensions`; única cabecera de autenticación `Authorization` |
| E29 | Configuración | `Dimensiones = 1024`; `MaxEntradasPorLote = 65`; `BaseUrl` `http://` remoto, relativa, con `userinfo` o con query; `Provider` desconocido; `OpenAICompatible` sin clave | I | La aplicación no arranca (`OptionsValidationException`); el mensaje no contiene la clave |
| E30 | | `http://localhost` sin clave; `Mock` con opciones de OpenAI vacías (entorno `Development`) | I | Arranca |
| E30b | | Entorno `Production` sin `AI:Embeddings:Provider` (por defecto `Mock`) o con `Mock` explícito; `Production` con `OpenAICompatible` y su clave | I | Los dos primeros no arrancan (mensaje sin secretos); el tercero arranca |
| E31 | | DI: `Provider = Mock` → `MockEmbeddingProvider`; `OpenAICompatible` → `OpenAICompatibleEmbeddingProvider` con su manejador de resiliencia | I | Resuelto el tipo esperado |
| E32 | | `appsettings*.json` versionados sin `ApiKey` con valor ni claves de precio o coste de embeddings | A | Verificado |
| E33 | Versionado | `FirmaEmbedding.De` para los dos proveedores | U | `openai-compatible:text-embedding-3-small@1536` y `mock:mock-bow-sha256-v1@1536` |
| E34 | No contaminación | Ningún consumidor de `IEmbeddingProvider` en `src` salvo el registro en DI; sin `BackgroundService` nuevo; sin escrituras de `Embedding`, `DocumentoFragmentos` ni `DocumentoIndices`; sin `AIUsageLogs.Add` nuevo; sin `SKIP LOCKED`, `<=>`, `websearch_to_tsquery` ni endpoints nuevos | A | Verificado (búsqueda en el código) |
| E35 | Regresión | Suites de las Fases 0–8.2 (incluido el chat con su manejador `ai-provider-retry` intacto) | I y E2E | Verdes |

### 19.2 Criterios PASS / FAIL

Cada criterio es binario. La 8.3 es PASS solo si se cumplen todos.

| # | Criterio | Verificación |
|---|---|---|
| C1 | `IEmbeddingProvider` y `EmbeddingPurpose` con la forma exacta de §9 del contrato rector; `EmbeddingBatchResult` con la forma de la adenda A-8.3-1 (§6.3) | Revisión |
| C2 | Cardinalidad, orden y dimensión garantizados; nunca vectores parciales, rellenados ni inventados | E1–E3, E19, E20 |
| C3 | Validación de entrada sin llamadas de red | E4 (0 llamadas) |
| C4 | Simulado determinista, independiente de la cultura, con golden fijado y similitud léxica | E5–E9 |
| C5 | Timeout de 60 s que incluye los reintentos; `EmbeddingProviderTimeoutException` | E22, E23 |
| C6 | Política fija del contrato rector: `MaxRetries = 2` (como máximo 3 intentos) ante 429, 503 y red; 1 intento ante cualquier otro código; retardo base de 1,5 s; ninguno de los dos configurable | E13–E17, E25, E25b (número de intentos y retardos exactos) |
| C7 | Clasificación explícita según §9 (`Motivo`, `EsTransitorio` derivado, `CodigoEstadoHttp`, `Intentos`, `EsperaSugerida`) | E14–E22, E25c |
| C7b | Consumo: `TokensEntrada` solo informado por el proveedor; `null` si no lo informó; sin estimaciones ni cálculo de coste en la 8.3 | E11, E12, E32 |
| C7c | Modelo: solicitado frente a declarado según §8.2; un declarado distinto se rechaza; nunca se sobrescribe | E12b, E33 |
| C8 | Cancelación del llamador propagada sin traducir, también durante el backoff | E10, E24 |
| C9 | Solo salen `model`, `input` y `encoding_format` (criterio 19 del contrato rector, en su parte de la 8.3) | E28 |
| C10 | Ninguna clave, texto ni vector en logs o excepciones | E26, E27 |
| C11 | Configuración validada al arrancar; HTTPS salvo loopback; `Mock` imposible en `Production`; sin secretos versionados | E29–E32, E30b |
| C12 | Sin códigos de error públicos nuevos | Revisión: búsqueda de constantes `AI_*` y `DOCUMENT_*` nuevas |
| C13 | Firma de embedding correcta y distinta para el simulado | E33 |
| C14 | No contaminación: nada de la 8.4 ni posteriores | E34 |
| C15 | Sin migraciones ni cambios del modelo de EF | `dotnet ef migrations has-pending-model-changes` |
| C16 | Sin paquetes nuevos (se reutiliza `Microsoft.Extensions.Http.Resilience`) | Diff de los `.csproj` |
| C17 | Proveedor de chat y `AI:OpenAICompatible` sin cambios | Diff de `OpenAICompatibleProvider.cs`, `OpenAIOptions.cs` y de su registro |
| C18 | `dotnet build --no-incremental` con 0 errores y 0 advertencias | Comando |
| C19 | Suite backend completa en verde dos veces | `dotnet test` ×2 |
| C20 | `dotnet list package --vulnerable --include-transitive` sin hallazgos | Comando |
| C21 | `git diff --check` limpio; contrato actualizado con los resultados; commit y push autorizados | Revisión |

## 20. Decisiones y contradicciones

### 20.1 Tabla final de contradicciones (X1–X5)

Ninguna se resuelve en silencio. Todas requieren tu confirmación.

| # | Texto o decisión del contrato rector | Decisión de la 8.3 | Motivo | Impacto | ¿Requiere modificar el contrato rector? |
|---|---|---|---|---|---|
| X1 | §3.2.2 y §9: sección propia `AI:Embeddings:{BaseUrl, ApiKey, ModelId, Dimensiones, TimeoutSeconds, MaxRetries, MaxEntradasPorLote}`. La instrucción de diseño pedía "no duplicar timeout, API key, modelo, endpoint ni retry settings" | Sección propia `AI:Embeddings`, como el contrato rector. `AI:OpenAICompatible` (chat) no se toca ni se reutiliza | Los embeddings pueden apuntar a otro servidor o usar otra clave que el chat (§3.2: TEI, vLLM, Ollama); no hay código duplicado, solo dos secciones con significados distintos | Dos claves de API posibles en despliegue | No: se cumple el contrato rector |
| X2 | §9: `MockEmbeddingProvider` determinista de bolsa de palabras "(pruebas y desarrollo)". La instrucción pedía que el simulado permitiera probar errores, timeout y retry | Simulado limpio, sin ganchos de fallo. Errores, timeout y retry se prueban con un transporte HTTP simulado y con un doble de prueba en el proyecto de pruebas (§16) | El timeout y el retry pertenecen al transporte; un gancho de fallo en código desplegado sería un punto de inyección | Ninguno funcional | No |
| X3 | §9: "lanza `AIProviderException` / `AIProviderTimeoutException` (códigos `AI_*` existentes)". La instrucción pedía un error de rate limit separado | Sin códigos públicos nuevos; el rate limit es `AI_PROVIDER_ERROR` con `Motivo = LimiteDeTasa` en `IFalloProveedorEmbeddings` (§9) | Respetar el catálogo público y el 502 de §3.2.2 | La 8.4 distingue por `Motivo`; los clientes HTTP de la 8.5 ven el mismo 502 | No |
| X4 | §3.2.2 y §9: `MaxRetries = 2` dentro de las opciones `AI:Embeddings` | **Valor 2 tratado como fijo**: constante `MaxReintentos = 2`, como máximo 3 intentos, sin clave configurable | Preservar exactamente la política contractual de como máximo 3 intentos e impedir que una configuración la amplíe (por ejemplo, a 5). **No se introduce semántica ni valor nuevos** | La lista de opciones de §9 se aplica salvo la configurabilidad de esta clave; el comportamiento es idéntico al valor por defecto del contrato rector | **Sí, de forma menor**: al actualizar el contrato rector (cierre 8.7), §9 debería indicar que `MaxRetries = 2` es fijo. Hasta entonces rige esta nota |
| X5 | §9: `EmbeddingBatchResult(…, int TokensEntrada, string ModelId, …)`, sin modelo declarado | **Adenda explícita A-8.3-1 (§6.3):** `int? TokensEntrada` (`null` = no informado; `0` es cero) y `string? ModeloDeclarado`; un declarado distinto se rechaza (`ModeloInesperado`); la firma usa el modelo efectivo | Con un `int`, el 0 sería indistinguible de un consumo real de 0 o habría que estimar; sin `ModeloDeclarado` se perdería la confirmación del proveedor | Solo el tipo de resultado, que todavía no tiene consumidores; la interfaz no cambia | **Sí**: §9 del contrato rector debe incorporar la adenda en su próxima actualización; mientras tanto, la adenda debe mantenerse alineada con él |

### 20.2 Decisiones D1–D12

**Estados:**
- **Aprobable:** lista para aprobar, sin dependencias.
- **Pendiente:** necesita una elección tuya.
- **Dependencia de 8.4:** la 8.3 deja la base y la decisión final es de la 8.4.
- **Dependencia del contrato rector:** descansa en la adenda A-8.3-1 o en la nota X4.

| # | Decisión | Propuesta (v1.2) | Estado |
|---|---|---|---|
| D1 | Proveedor por defecto y producción | `AI:Embeddings:Provider = Mock` por defecto **solo fuera de `Production`**. En `Production`, `Mock` (explícito o por defecto) impide arrancar, con la validación al arrancar ya usada en la 6.X. **Alternativa:** aplicar la regla de producción desde la 8.4, cuando exista el primer consumidor, para que los despliegues actuales no tengan que configurar los embeddings antes de usarlos | **Pendiente**: elegir si la regla rige desde la 8.3 (recomendado) o desde la 8.4 |
| D2 | Retardo base del backoff | **1,5 s constante, no configurable** (contractual por el patrón del chat de `FASE_6X_CONTRATO.md`) | **Aprobable** |
| D3 | Clasificación de errores | `EmbeddingProviderException` y `EmbeddingProviderTimeoutException` con `IFalloProveedorEmbeddings` (`Motivo`, `EsTransitorio` derivado, `CodigoEstadoHttp`, `Intentos`, `EsperaSugerida`); mismos códigos públicos | **Aprobable** (qué hace la 8.4 con cada motivo es de la 8.4) |
| D4 | Lote inválido | `ArgumentException` antes de la red | **Aprobable** |
| D5 | Vectores con NaN, ∞ o norma 0 | `RespuestaInvalida` | **Aprobable** |
| D6 | Respuesta sin `usage` | `TokensEntrada = null`; sin estimación en la 8.3 | **Dependencia del contrato rector** (adenda A-8.3-1, X5). La estimación, si existe, es **dependencia de 8.4** |
| D7 | Endpoint y payload | HTTPS salvo loopback; sin redirecciones; respuesta de 16 MiB como máximo | **Aprobable** |
| D8 | Clave ausente | Obligatoria con `OpenAICompatible` salvo loopback | **Aprobable** |
| D9 | Coste | Sin precio funcional ni opción en la 8.3; 0,02 USD por millón solo como referencia que debe verificarse oficialmente antes de producción | **Dependencia de 8.4** (política de costes de la indexación; la 8.5 para la búsqueda) |
| D10 | Firma del embedding | Helper `FirmaEmbedding.De(provider)`, sin cambiar la interfaz | **Aprobable** |
| D11 | Reintentos | `MaxRetries = 2` del contrato rector, fijo: como máximo 3 intentos | **Dependencia del contrato rector** (nota X4) |
| D12 | Modelo solicitado y declarado | Igual o ausente → aceptado; distinto → `ModeloInesperado`; firma con el efectivo | **Dependencia del contrato rector** (adenda A-8.3-1, X5) |

**Observaciones para la 8.4** (no se deciden aquí):
- cómo registrar un consumo no informado (`TokensEntrada = null`) y si se estima, marcado como estimación, para el tope diario de tokens;
- política de costes y verificación oficial del precio antes de producción (D9);
- consumo de los intentos fallidos sin tokens medibles (§14);
- correspondencia entre `MotivoFalloEmbedding` y `CodigoError` del índice;
- relación del timeout del lote (60 s) con el lease de 300 s.

**Observación general (fuera de la 8.3):** el proveedor de chat (`AI:Provider`) también usa `Mock` por defecto sin protección en producción. No se cambia aquí.

## 21. Archivos esperados en la implementación (referencia; no se crean ahora)

| Archivo | Cambio |
|---|---|
| `Application/Common/Interfaces/AI/IEmbeddingProvider.cs` | Nuevo: interfaz, `EmbeddingPurpose`, `EmbeddingBatchResult` (§6, X5), `FirmaEmbedding` |
| `Application/Common/Exceptions/EmbeddingProviderExceptions.cs` | Nuevo: `MotivoFalloEmbedding`, `IFalloProveedorEmbeddings`, `EmbeddingProviderException`, `EmbeddingProviderTimeoutException` (sin tocar las excepciones existentes) |
| `Infrastructure/Services/AI/EmbeddingOptions.cs` | Nuevo, con su validador |
| `Infrastructure/Services/AI/OpenAICompatibleEmbeddingProvider.cs` | Nuevo |
| `Infrastructure/Services/AI/MockEmbeddingProvider.cs` | Nuevo |
| `Infrastructure/DependencyInjection.cs` | Opciones con `ValidateOnStart`, `AddHttpClient<OpenAICompatibleEmbeddingProvider>` con `AllowAutoRedirect = false`, límite de respuesta y `AddResilienceHandler("ai-embeddings-retry")`, y selección del proveedor. **Sin tocar** el bloque del chat |
| `tests/.../Fase8/Fase83*.cs` | Pruebas de §19.1 y el doble `ProveedorEmbeddingsProgramable` |

Migraciones: **0**. Paquetes: **0**. Docker y PostgreSQL: **sin cambios**. Endpoints y workers: **ninguno**.
