# Fase 6.X — Contrato de remediación IA (v1.1)

Contrato aprobado para la remediación previa a la Fase 8. Resuelve tres bloqueadores de la Fase 6:

- **D-2**: acceso del AsistenteLegal al contenido documental a través de la IA.
- **X1**: documentos atascados en `Procesando`.
- **H10**: extracción de texto.

**Estado:** diseño aprobado. Las decisiones DA-1 a DA-13 están aprobadas (DA-13 = variante A). La **implementación todavía no está autorizada**.

Base: commit `4ad6af8` (Fase 7 cerrada).

## 1. Estado de partida

| Bloqueador | Evidencia |
|---|---|
| **D-2** | `SummarizeExpedienteAsync` (`AIService.cs:547`) solo autoriza el expediente. Para el AsistenteLegal no comprueba la tarea vigente y lee el contenido de todos los documentos del expediente |
| **X1** | `ExtractFromDocumentAsync` persiste `Procesando` (`AIService.cs:802`) antes de llamar al proveedor. Una caída o cancelación antes del guardado final (`:883`) lo deja así indefinidamente, sin mecanismo de recuperación |
| **H10** | `ReadDocumentTextSafelyAsync` (`AIService.cs:1212`) lee los bytes crudos con `StreamReader`, sin extraer texto. Captura todas las excepciones (incluidas las de seguridad y la cancelación) y devuelve un texto inventado ("Archivo procesado.") |

**Hechos del sistema que condicionan este contrato:**
- El timeout del proveedor es un límite duro de **60 s que incluye los reintentos**: un único `CancelAfter` envuelve la llamada, y Polly hace hasta 2 reintentos con backoff exponencial (base 1,5 s) y jitter.
- La extracción de hechos no usa streaming (`CompleteChatAsync`).
- La base de datos reintenta con `EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: 10 s)`.
- `AuditService` descarta el evento si no hay tenant en el contexto. Un worker debe hacer `SetTenantId` por tenant.
- Ya existe un patrón de worker multi-instancia: `AlertasBackgroundService` (scope por tenant, `SetTenantId`, `pg_try_advisory_xact_lock`, estrategia de ejecución de EF).

## 2. D1 — Conversaciones

| Operación | Contrato |
|---|---|
| `GET conversaciones/{id}` y `GET conversaciones` | Sin cambios: propiedad (Junior y AsistenteLegal solo ven las suyas) más PBAC. No se borra el historial |
| `POST conversaciones/{id}/mensajes`, `POST chat` con `ConversationId`, streaming | Comprueban el acceso al expediente con `EnsureCanAccessExpedienteAsync`. Expediente eliminado → 404. Otro tenant, o Junior no responsable → 403 |
| Expediente eliminado en GET | Se mantiene la respuesta actual |

**Reglas:**
- Los caminos de chat (`SendMessageAsync`, `StreamMessageAsync`, `CreateConversationAsync`) **nunca** leen documentos ni llaman al extractor. Una conversación antigua no puede servir para obtener contenido documental nuevo.
- No se vuelven a procesar documentos antiguos de forma automática.
- **DA-1 (aprobada):** a la continuación del chat no se le aplica la regla de la tarea vigente del AsistenteLegal. El chat no lee documentos y la regla aprobada se refiere a documentos.

## 3. D2 — DocumentoIds en `resumir-expediente`

| Regla | Detalle |
|---|---|
| Cuándo se valida | Después de la autorización (D11, paso 1): un 403/404 de acceso tiene prioridad |
| `DocumentoIds` nulo o vacío | Se usan todos los documentos activos del expediente |
| Duplicados | Se eliminan sin error |
| Id válido | Existe, no está eliminado y pertenece al expediente solicitado en el tenant actual. Un documento en `Procesando` es válido |
| Id no válido | No existe, está eliminado, pertenece a otro expediente u otro tenant, o es `Guid.Empty` |
| Algún id no válido | **HTTP 400**, sin llamar al proveedor ni crear conversación o `AIUsageLog` |
| Cuerpo | `errors: ["DOCUMENT_DOCUMENTS_INVALID", "El documento <id> no es válido para este expediente.", …]`, un mensaje por id no válido, en el orden enviado |
| Fuga de información | Mensaje idéntico para todas las causas; solo se devuelven ids que envió el propio cliente |
| Mecanismo | `ValidationException` con `ErrorCode`: el middleware de la Fase 7.2 ya emite `[código, ...mensajes]` |
| Código | `DOCUMENT_DOCUMENTS_INVALID`, nuevo, declarado fuera de `DocumentoErrorCodes` (Fase 7) |

## 4. D3 — X1: recuperación de `Procesando`

**Opción aprobada: A, lease o timeout automático.** El desbloqueo administrativo (opción B) queda fuera.

### Columna (DA-2 = A1)

| Atributo | Valor |
|---|---|
| Nombre | `Documento.IaProcesandoDesde` |
| Tipo | `timestamp with time zone`, nulable |
| Significado | Instante en que el documento entró en `Procesando`. Solo tiene valor mientras `EstadoIa = Procesando` |
| Se fija | **Obligatorio** en toda transición nueva a `Procesando`, en el **mismo UPDATE** (con xmin) que la transición. Ningún código nuevo puede dejar un documento en `Procesando` con `IaProcesandoDesde = NULL` |
| Se limpia (NULL) | En toda salida de `Procesando`: a `Procesado`, a `Fallido` o por recuperación |
| Histórico | Sin rellenar datos. `COALESCE(IaProcesandoDesde, UpdatedAt, CreatedAt)` es un fallback **exclusivo para los documentos históricos** que ya estaban en `Procesando` con `IaProcesandoDesde = NULL` antes de la migración. **No** sustituye a `IaProcesandoDesde` en los procesos nuevos. Un `NULL` en un proceso nuevo es un defecto (prueba X1-N1) |
| Coste | Una migración sobre `documentos` y cambios en `Documento.cs` y `DocumentoConfiguration.cs`. Es una excepción aprobada a D14 |

### Lease (DA-3)

| Componente | Valor | Naturaleza |
|---|---|---|
| Extracción de texto | 30 s | **Timeout explícito** (`AI:Extraction:TimeoutSeconds`) |
| Proveedor (con 2 reintentos y backoff) | 60 s | **Timeout explícito** (límite duro actual) |
| Persistencia final (`SaveChanges`) | ~60 s | **Margen operacional estimado**; NO es un máximo garantizado |
| Referencia operacional (T) | ~150 s | 30 + 60 + margen de persistencia |
| **Lease** | **300 s** | Margen operacional deliberadamente superior a T |

**Notas vinculantes:**
- Los únicos límites duros son el timeout de extracción (30 s) y el del proveedor (60 s, con reintentos).
- Los ~150 s **no constituyen un máximo matemático garantizado**: EF Core y Npgsql no acotan la duración total de `SaveChanges`.
- `EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: 10 s)` **no garantiza por sí solo** un máximo de 60 s, porque limita la espera entre reintentos, no el tiempo de cada comando ni de la conexión.
- El lease de 300 s es una decisión operacional, deliberadamente mayor que los timeouts explícitos, para proteger contra pausas de GC, latencias de red o de base de datos y condiciones transitorias.
- Si una operación legítima superara el lease, la protege xmin: 409 y resultado descartado. No se sobrescribe nada.

**Configuración:**

| Clave | Valor |
|---|---|
| `AI:Extraction:TimeoutSeconds` | 30 |
| `AI:ProcessingRecovery:LeaseSeconds` | 300 |
| `AI:ProcessingRecovery:IntervalSeconds` | 60 |

**Validación al arrancar:** `LeaseSeconds > TimeoutSeconds del proveedor + AI:Extraction:TimeoutSeconds`. Es el único mínimo demostrable; si no se cumple, la aplicación no arranca. El valor por defecto (300 s) supera ese mínimo (90 s) con holgura.

### Comportamiento

| Aspecto | Contrato |
|---|---|
| Quién recupera | Solo el worker `ProcesamientoIaRecoveryBackgroundService`; no hay endpoint |
| Condición | `EstadoIa = Procesando` y `COALESCE(IaProcesandoDesde, UpdatedAt, CreatedAt) < now() - LeaseSeconds` (el fallback solo actúa en el histórico, ver la columna) |
| Transición | `Procesando → Fallido`, con `IaProcesandoDesde = NULL`, `UpdatedAt = now` y `UpdatedBy = "system:ia-recovery"` |
| Motivo | `LEASE_EXPIRED` |
| Auditoría | `AI_PROCESSING_RECOVERED` (§13), con `LogInTransactionAsync` en el mismo `SaveChanges` que la transición |
| Concurrencia | UPDATE con `WHERE xmin = @leído`. Si afecta 0 filas, se omite sin error |
| Operación todavía activa | No se recupera antes del lease. Si termina después, ver la fila siguiente |
| Proveedor terminado, pero el `SaveChanges` final falla por xmin (`DbUpdateConcurrencyException`) | Ver el detalle debajo de esta tabla |
| Reinicio del proceso | El documento queda en `Procesando` y el worker lo recupera al vencer el lease |
| Cancelación | §10 |
| Reintentos del proveedor | Dentro del límite de 60 s, sin efecto adicional |
| Ejecución sin duplicados | Ver el detalle debajo de esta tabla |
| Reintento posterior | `Fallido → Procesando` permitido |

**Proveedor terminado, pero el `SaveChanges` final falla por xmin:**
1. **Se descarta** el resultado de la IA: no se persisten `MetadatosJson` ni `Procesado`.
2. **No se sobrescribe** el estado que estableció la otra operación (recuperación, cancelación u otra):
   - no se reintenta el guardado;
   - se desacoplan las entidades del contexto fallido (`ChangeTracker.Clear()`) y no se vuelven a usar para escribir.
3. Respuesta **409 `DOCUMENT_CONCURRENCY_CONFLICT`**.
4. Se registra un `AIUsageLog` **fallido** en un **`DbContext` independiente** (scope nuevo con `SetTenantId`, después del fallo; mismo patrón que `AuditService.LogAsync`). **Nunca** dentro del contexto ni de la transacción que acaban de fallar.
5. Ese registro indica que el proveedor **sí se consumió** y que la operación terminó por conflicto:
   - `Exitoso = false`;
   - `CodigoError = "DOCUMENT_CONCURRENCY_CONFLICT"`;
   - `TokensEntrada`, `TokensSalida`, `TotalTokens` y `CostoEstimadoUsd` **reales** de la respuesta;
   - `ModelId` y `ProviderId` de la respuesta.
6. Si ese registro también falla: log `[AI_USAGE_NO_REGISTRADO]` con id del documento, tenant y tipo de error. La respuesta 409 no cambia.

**Ejecución sin duplicados (worker):**
- **Ciclo:** cada `IntervalSeconds`.
- **Consulta global:** con `IgnoreQueryFilters` y predicado explícito, obtiene los tenants que tienen documentos vencidos.
- **Por cada tenant:**
  - scope nuevo y `SetTenantId` (necesario para la auditoría);
  - `pg_try_advisory_xact_lock` con un espacio de claves propio;
  - lote de hasta 100 documentos;
  - todo dentro de `CreateExecutionStrategy().ExecuteAsync`.
- **Varias instancias:** el advisory lock evita que dos trabajen el mismo tenant a la vez; xmin garantiza que cada fila se recupere una sola vez.

## 5. D4 — Separación con la Fase 8

| Fase 6.X (dentro) | Fase 8 (fuera) |
|---|---|
| Extractor de texto real con resultado tipado (§8) | Chunking |
| Ningún contenido inventado | Embeddings |
| Distinción de errores y mapeo HTTP | Base vectorial |
| Cancelación correcta | Indexación y reindexación |
| TXT, PDF, DOCX y XLSX | Búsqueda semántica y RAG |
| Integración en `SummarizeExpediente`, `SummarizeDocumento`, `Extract` y `Draft` | Persistir el texto extraído |
| Límite de caracteres sin leer el documento entero | OCR |

## 6. D5 — Librerías (DA-4)

**Aprobadas** (su instalación requiere todavía la autorización de implementación):

| Librería | Versión de referencia | Licencia | Uso |
|---|---|---|---|
| UglyToad.PdfPig | 0.1.16 (22-08-2026) | Apache-2.0 | PDF (`ContentOrderTextExtractor`). C# puro, sin dependencias nativas en net6+. Sin OCR |
| DocumentFormat.OpenXml | 3.5.1 (18-03-2026) | MIT | DOCX y XLSX. Microsoft y .NET Foundation; targets net8 y net10 |

**Descartadas:**

| Librería | Motivo |
|---|---|
| NPOI 2.8.x | Desde la 2.8.0, EULA con tarifa mensual de mantenimiento para organizaciones con ingresos de 10.000 USD anuales o más; dependencias nativas (SkiaSharp); no documenta extracción de DOC |
| Docnet.Core | Basada en PDFium nativo (paquete de 17,6 MB); sin publicaciones desde 2023 |
| iText | AGPL o licencia comercial |

**Condiciones de implementación:**
- `dotnet list package --vulnerable` sin hallazgos.
- Versiones fijas.

**Medidas del extractor:**
- Tamaño ya limitado a 25 MiB.
- Timeout de extracción.
- Corte en 30.001 caracteres.
- OOXML: no se procesan partes externas ni macros, y la lectura se limita para frenar las bombas de descompresión.
- PDF cifrado: no se prueban contraseñas.

## 7. D6 — Formatos

El formato se decide por el `ContentType` canónico guardado en la Fase 7, nunca por lo que declare el cliente. Los formatos no soportados nunca producen texto.

| Formato | Fase 6.X | Comportamiento |
|---|---|---|
| PDF | Sí (PdfPig) | Texto en orden de lectura. Sin capa de texto (escaneado) → `Empty`. Cifrado o dañado → `InvalidContent` |
| DOCX | Sí (OpenXml) | Párrafos del cuerpo y tablas. Dañado → `InvalidContent` |
| XLSX | Sí (OpenXml, DA-5) | Por hoja y fila, celdas separadas por tabulador, con resolución de *shared strings*. Fórmulas: se usa el valor guardado en caché |
| DOC | No | `UnsupportedFormat` |
| XLS | No | `UnsupportedFormat` |
| TXT | Sí (sin librería) | UTF-8 estricto, con las reglas de la Fase 7.2 |
| JPG/JPEG | No (sin OCR) | `UnsupportedFormat` |
| PNG | No (sin OCR) | `UnsupportedFormat` |

## 8. D7 — ExtractionResult

Contrato interno: `ExtractionResult { Status, Text?, DocumentoId }`.

| Estado | Significado | HTTP | Código |
|---|---|---|---|
| `Success` | Texto no vacío, de 30.000 caracteres o menos | — | — |
| `Empty` | Formato soportado sin texto | 422 | `DOCUMENT_TEXT_EMPTY` (nuevo) |
| `FileNotFound` | `NotFoundException` del almacenamiento, o registro sin ruta | 404 | `DOCUMENT_FILE_NOT_FOUND` (existente) |
| `Forbidden` | Ruta insegura, symlink o punto de reanálisis | 403 | Sin código, igual que la descarga de la Fase 7 (DA-6) |
| `UnsupportedFormat` | DOC, XLS, JPG o PNG | 422 | `DOCUMENT_TEXT_UNSUPPORTED` (nuevo) |
| `InvalidContent` | PDF o DOCX dañado, cifrado, o UTF-8 inválido | 422 | `DOCUMENT_TEXT_INVALID` (nuevo) |
| `ExtractionFailed` | Timeout de extracción, o error inesperado del parser o de E/S | 422 | `DOCUMENT_TEXT_EXTRACTION_FAILED` (nuevo); se registra solo el tipo de excepción |
| `ContextExceeded` | Más de 30.000 caracteres | 422 | `DOCUMENT_EXCEEDS_CONTEXT_LIMIT` (existente) |
| `Cancelled` | **No es un valor devuelto**: la `OperationCanceledException` del token del llamador se propaga sin alterar (§10) | — | — |

**Reglas:**
- **Nunca** se devuelve "Archivo procesado." ni ningún otro texto de respaldo.
- **DA-7 (aprobada):** `GlobalExceptionMiddleware` emite el `ErrorCode` de las `BusinessRuleException` (422) en `errors`. Es un cambio aditivo, que también afecta a `DOCUMENT_EXCEEDS_CONTEXT_LIMIT` y `AI_CONTEXT_WINDOW_EXCEEDED`.
- **DA-12 (aprobada):** códigos nuevos `DOCUMENT_DOCUMENTS_INVALID`, `DOCUMENT_TEXT_EMPTY`, `DOCUMENT_TEXT_UNSUPPORTED`, `DOCUMENT_TEXT_INVALID` y `DOCUMENT_TEXT_EXTRACTION_FAILED`.

## 9. D8 — Máquina de estados (solo `ExtractFromDocumentAsync`)

```
Pendiente ─┐
Procesado ─┼─(extract autorizado; formato soportado)─▶ Procesando ─(texto real y respuesta válida)─▶ Procesado
Fallido  ──┘                                              │
                                                          ├─(cualquier fallo de esta tabla)──────────▶ Fallido
                                                          └─(lease vencido, worker)────────────────▶ Fallido
Procesando ─(extract)─▶ 409 DOCUMENT_PROCESSING (sin cambio)
```

**Comprobación previa, sin cambiar el estado:**
1. Autorización con `EnsureCanAccessDocumentoAsync` (regla del AsistenteLegal incluida).
2. Formato soportado según el `ContentType`: si no lo es, 422 `DOCUMENT_TEXT_UNSUPPORTED` y el estado no cambia.
3. Documento en `Procesando`: 409 `DOCUMENT_PROCESSING`.

| Situación (estando en `Procesando`) | Estado final | Respuesta |
|---|---|---|
| Archivo inexistente | Fallido | 404 `DOCUMENT_FILE_NOT_FOUND` |
| Ruta insegura o symlink | Fallido | 403 con log de seguridad |
| Extracción vacía | Fallido | 422 `DOCUMENT_TEXT_EMPTY` |
| PDF o DOCX dañado | Fallido | 422 `DOCUMENT_TEXT_INVALID` |
| Timeout de extracción | Fallido | 422 `DOCUMENT_TEXT_EXTRACTION_FAILED` |
| Contexto excedido (documento o tokens) | Fallido | 422 `DOCUMENT_EXCEEDS_CONTEXT_LIMIT` / `AI_CONTEXT_WINDOW_EXCEEDED` |
| Timeout del proveedor | Fallido | 502 `AI_PROVIDER_TIMEOUT` |
| Fallo del proveedor | Fallido | 502 `AI_PROVIDER_ERROR` |
| Cancelación del usuario | Fallido (si se puede guardar; si no, el lease lo resuelve) | Excepción propagada |
| Recuperado por el lease mientras seguía activo | El que estableció la otra operación | 409 `DOCUMENT_CONCURRENCY_CONFLICT`; resultado descartado; `AIUsageLog` fallido independiente (§4) |
| Éxito con texto real | **Procesado** + `MetadatosJson` | 200 |

**Invariantes:**
- **Nunca** se pasa de `Procesando` a `Procesado` sin `ExtractionResult.Success`.
- Toda salida de `Procesando` limpia `IaProcesandoDesde`.
- **DA-8:** `MetadatosJson` no se modifica en un fallo; conserva la propuesta anterior, si existía.
- Las transiciones de D8 pertenecen **exclusivamente** a `ExtractFromDocumentAsync` y al worker de recuperación (DA-13).

## 10. D9 — Cancelación

- **No se capturan como errores normales** `OperationCanceledException` ni `TaskCanceledException` cuando el token del llamador está cancelado:
  - los `catch (Exception)` deben excluirlas;
  - el timeout interno del proveedor se sigue traduciendo a `AI_PROVIDER_TIMEOUT`.
- **El token se propaga** a la apertura del archivo, al extractor (enlazado con su timeout), al proveedor y a `SaveChanges`.
- **Extract cancelado en `Procesando`:**
  - se intenta una vez pasar a `Fallido` con `CancellationToken.None`;
  - ese guardado usa xmin y su auditoría es `AI_EXTRACTION_FAILED` con causa `CANCELLED`;
  - después se vuelve a lanzar la excepción;
  - si ese guardado falla, el lease lo recupera;
  - no queda en `Procesando` a propósito.
- **Reintento:** sí, `Fallido → Procesando` está permitido.
- **Resumen y borrador** (no cambian el estado de los documentos):
  - la cancelación se propaga;
  - no se crea conversación ni `AIUsageLog` exitoso;
  - se mantiene el `AIUsageLog` fallido del proveedor.

## 11. D10 — Seguridad

| Origen | Tratamiento |
|---|---|
| `NotFoundException` de `FileStorageService` | `FileNotFound` → 404 `DOCUMENT_FILE_NOT_FOUND`. Log `[DOCUMENT_FILE_NOT_FOUND]` con id del documento y tenant, sin ruta |
| `ForbiddenException` (traversal, ruta absoluta o inválida) | `Forbidden` → **la operación se aborta** (403). Nunca se continúa ni se envía nada al LLM. Log de error `[AI_DOCUMENT_STORAGE_FORBIDDEN]` con id del documento, además del log de seguridad del almacenamiento |
| Symlink o punto de reanálisis | Igual que la fila anterior |
| Archivo dañado | `InvalidContent` → 422, sin el mensaje del parser en la respuesta |
| `ValidationException` por ruta vacía | `FileNotFound` → 404 |
| Otra excepción de E/S | `ExtractionFailed` → 422, registrando solo el tipo de excepción |
| Contenido extraído | Siempre pasa por `PromptSanitizer.WrapUntrustedContent` |

`FileStorageService` no se modifica. Se elimina el `catch (FileNotFoundException)`, que es código muerto.

## 12. D11 — SummarizeExpedienteAsync

**`SummarizeExpedienteAsync` obtiene el contenido mediante el extractor tipado, pero no modifica `Documento.EstadoIa` ni `Documento.MetadatosJson`.** Las transiciones de D8 pertenecen exclusivamente a `ExtractFromDocumentAsync`. El resultado de extracción que se usa durante el resumen no provoca por sí mismo una transición persistente de `EstadoIa` desde el flujo de resumen (DA-13 = variante A).

**Flujo:**
1. **Autorización:**
   - `EnsureNotSuperAdmin` (403);
   - `EnsureCanAccessDocumentosDeExpedienteAsync(expedienteId)`, que sustituye a `EnsureCanAccessExpedienteAsync`. Comprueba tenant, expediente activo (404), reglas del Junior y regla de la tarea vigente del AsistenteLegal (403);
   - un 403 se traduce a `DOCUMENT_ACCESS_DENIED`; el 404 del expediente va sin código;
   - todo ocurre **antes** de cualquier lectura de documentos.
2. **Validación de `DocumentoIds`** (§3) → 400 `DOCUMENT_DOCUMENTS_INVALID`.
3. **Carga de documentos:** activos del expediente autorizado (tenant explícito), filtrados por los ids validados.
4. **Acceso al archivo:** solo por `RutaAlmacenamiento` de la base de datos, a través de `FileStorageService`.
5. **Extracción por documento:**
   - cada documento se extrae **por separado y en secuencia**, y produce su `ExtractionResult`;
   - no se abre ninguna transacción de base de datos que abarque la extracción física de los documentos; la extracción no ocurre dentro de ninguna transacción.
6. **Validación de resultados:** si **cualquier** documento devuelve un resultado distinto de `Success` (DA-9):
   - el resumen completo falla con el error de §8 de ese documento;
   - **no se llama al proveedor**;
   - no se crea conversación ni `AIUsageLog` exitoso;
   - no se modifica `EstadoIa` ni `MetadatosJson` de ningún documento por causa del resumen.
7. **Construcción del contexto:** `WrapUntrustedContent` por documento y validación de los tokens totales (422).
8. **Proveedor:** cancelación según §10.
9. **Persistencia:** conversación, mensaje y `AIUsageLog`, en un solo `SaveChanges`. Ninguna escritura sobre `documentos`.

**Mismo patrón de autorización y extracción en `SummarizeDocumentoAsync` y `DraftEscritoAsync`:**
- por documento, `EnsureCanAccessDocumentoAsync` más el extractor tipado;
- se añade la traducción a `DOCUMENT_NOT_FOUND` y `DOCUMENT_ACCESS_DENIED`;
- tampoco modifican `EstadoIa` ni `MetadatosJson`.

**Garantía:** el AsistenteLegal sin tarea vigente no llega nunca al paso 3, y ningún camino de IA lee contenido sin una autorización documental.

## 13. D12 — Auditoría

| Evento | Cuándo | Mecanismo | Contenido permitido |
|---|---|---|---|
| `AI_PROCESSING_RECOVERED` | El worker pasa un documento de `Procesando` a `Fallido` | `LogInTransactionAsync` en la misma transacción; `SetTenantId`; `UsuarioId` nulo (sistema) | `expedienteId`, `procesandoDesde`, `motivo = "LEASE_EXPIRED"` |
| `AI_EXTRACTION_FAILED` | `ExtractFromDocumentAsync` pasa a `Fallido` | `LogInTransactionAsync` con el guardado de `Fallido` | `expedienteId`, `causa` (estado de §8, o `PROVIDER_ERROR`, `PROVIDER_TIMEOUT`, `CANCELLED`) |
| `AI_EXTRACTION_COMPLETED` | `ExtractFromDocumentAsync` pasa a `Procesado` (DA-10) | `LogInTransactionAsync` con el guardado final | `expedienteId`, `modelId` |
| Documento no encontrado, acceso denegado | — | Sin auditoría (coherente con D74-9); solo logs técnicos | — |
| Fallos de extracción en resumen o borrador | — | Solo log técnico (no cambian el estado) | id del documento, causa |

**Nunca se registran:**
- contenido ni texto extraído;
- rutas;
- prompts;
- respuestas del modelo;
- `MetadatosJson`;
- secretos ni tokens;
- mensajes de excepción.

## 14. D13 — Matriz de pruebas

**Infraestructura:**
- PostgreSQL real y `FileStorageService` real (directorio temporal).
- Los PDF, DOCX y XLSX válidos se generan dentro de la propia prueba, sin binarios en el repositorio.
- **DA-11 (aprobada):** se adaptan las pruebas de la Fase 6 que dependen del texto de respaldo o del doble de almacenamiento que devuelve texto plano. El inventario exacto se hará durante la implementación.

| Bloque | Caso | Esperado |
|---|---|---|
| **D-2** (servicio y API `resumir-expediente`) | Sin tarea | 403 `DOCUMENT_ACCESS_DENIED`; 0 llamadas al proveedor; 0 conversaciones; 0 `AIUsageLog` exitosos |
| | Pendiente / EnProgreso | 200 |
| | Completada / Cancelada | 403 |
| | Tarea de otro usuario / otro expediente | 403 |
| | Otro tenant | 403 |
| | SuperAdmin | 403 |
| | Expediente eliminado | 404 |
| | Junior no responsable | 403 (regresión) |
| **D1** | Chat, mensaje y streaming no abren ningún archivo | Doble del almacenamiento que falla si se llama a `OpenRead` |
| | Reanudar con el expediente eliminado | 404 |
| | GET de conversación propia tras perder la tarea | 200 (historial intacto) |
| **DocumentoIds** | Válidos | 200, solo esos documentos en el prompt |
| | Inexistente / eliminado / de otro expediente / de otro tenant / `Guid.Empty` | 400 `DOCUMENT_DOCUMENTS_INVALID` con el id y mensaje idéntico |
| | Válido + inválido | 400; sin proveedor |
| | Duplicados | 200 |
| | Sin autorización + ids inválidos | 403 (prioridad) |
| **Resumen con varios documentos** | S-M1: 3 documentos; el tercero falla (por ejemplo, PDF dañado) | Error de §8 del tercero; **0 llamadas al proveedor**; sin conversación ni `AIUsageLog` exitoso; `EstadoIa` y `MetadatosJson` de los 3 **sin cambios** |
| | S-M2: transacciones durante la extracción | Interceptor de transacciones: 0 transacciones abiertas durante la fase de extracción |
| | S-M3: resumen correcto | `EstadoIa` y `MetadatosJson` de todos los documentos sin cambios |
| **X1** | Operación normal | `Procesado`, `IaProcesandoDesde` nulo |
| | X1-N1: Extract nuevo, fila leída desde otra conexión tras la transición a `Procesando` y antes del proveedor | `IaProcesandoDesde` no nulo, igual al instante de la transición (con tolerancia); nunca NULL |
| | X1-N2: toda salida de `Procesando` (Procesado, Fallido, recuperación) | `IaProcesandoDesde = NULL` |
| | Caída simulada (sembrar `Procesando` con inicio antiguo) | El worker lo pasa a `Fallido` y audita `AI_PROCESSING_RECOVERED` |
| | Dentro del lease | No se recupera |
| | Histórico con `IaProcesandoDesde` NULL y `UpdatedAt` antiguo / reciente | Se recupera / no se recupera |
| | Cancelación durante el proveedor | `Fallido` y auditoría `CANCELLED` |
| | Timeout del proveedor | `Fallido`, 502 |
| | Dos workers a la vez | Exactamente una transición y un evento |
| | X1-C1: el proveedor responde y un interceptor cambia la fila desde otra conexión antes del `SaveChanges` final | 409 `DOCUMENT_CONCURRENCY_CONFLICT`; estado y `MetadatosJson` los de la otra operación; **existe** un `AIUsageLog` con `Exitoso = false`, `CodigoError = "DOCUMENT_CONCURRENCY_CONFLICT"` y tokens/coste iguales a los de la respuesta; ningún `AIUsageLog` exitoso |
| | X1-C2: igual que X1-C1, pero también falla el contexto independiente del registro | 409 sin cambios; log `[AI_USAGE_NO_REGISTRADO]` |
| | Reintento tras `Fallido` | Permitido; `Procesado` |
| | Worker con tenant establecido | Auditoría presente (verifica `SetTenantId`) |
| | Validación del lease al arrancar | Lease menor que el mínimo → error de arranque |
| **H10** | TXT / PDF / DOCX / XLSX válidos | `Success` con el texto esperado |
| | PDF dañado / DOCX dañado / PDF cifrado | `InvalidContent` |
| | PDF sin texto / archivo vacío | `Empty` |
| | Inexistente | `FileNotFound` |
| | Ruta insegura / symlink | `Forbidden`; proveedor no llamado |
| | DOC / XLS / PNG / JPG | `UnsupportedFormat` |
| | Más de 30.000 caracteres | `ContextExceeded` sin leer el documento completo |
| | Cancelación | `OperationCanceledException` propagada |
| | Timeout del extractor | `ExtractionFailed` |
| | Ningún camino devuelve "Archivo procesado." | Búsqueda de la cadena en el código más aserciones |
| **Extract** | Cualquier resultado distinto de `Success` | Nunca `Procesado`; `MetadatosJson` sin cambios |
| | Formato no soportado | 422 sin cambio de estado |

## 15. D14 — Fuera de alcance

Esta fase **no** modifica:
- `FileStorageService`;
- `DocumentoService`;
- `DocumentosController`;
- `docs/FASE_7_CONTRATO.md`;
- RAG, embeddings, base vectorial, chunking, indexación, búsqueda semántica ni OCR.

**Excepciones aprobadas:**
- **DA-2:** columna `IaProcesandoDesde` (`Documento.cs`, `DocumentoConfiguration.cs` y una migración).
- **DA-7:** `ErrorCode` de las `BusinessRuleException` en `GlobalExceptionMiddleware`.

Los códigos nuevos se declaran fuera de `DocumentoErrorCodes`.

## 16. D15 — Criterio de entrada a la Fase 8

La Fase 8 solo puede empezar cuando se cumpla todo lo siguiente:

- [ ] D-2 resuelto: la matriz D-2 está verde e incluye la prueba de API con 0 llamadas al proveedor.
- [ ] X1 resuelto: lease, worker, auditoría y concurrencia probados, incluidos X1-N1, X1-C1 y los dos workers a la vez.
- [ ] H10 resuelto: extractor tipado; no queda ninguna cadena de respaldo en el código.
- [ ] Extracción real probada con TXT, PDF, DOCX y XLSX contra el `FileStorageService` real.
- [ ] Ningún error de almacenamiento (`NotFound`, `Forbidden`, E/S) produce texto ni llama al proveedor.
- [ ] La cancelación se propaga y deja `Fallido` o el lease, nunca `Procesado`.
- [ ] Todo camino de IA que lee contenido pasa por una autorización documental.
- [ ] `Procesado` solo con `Success`; el resumen no modifica `EstadoIa` ni `MetadatosJson`.
- [ ] Suite backend completa en verde, dos ejecuciones; Angular en verde.
- [ ] `dotnet build --no-incremental`: 0 errores y 0 advertencias.
- [ ] `has-pending-model-changes` limpio, con la migración aprobada aplicada.
- [ ] `dotnet list package --vulnerable` sin hallazgos.
- [ ] Este contrato actualizado con el resultado de la implementación, y commit y push autorizados.

## 17. Decisiones aprobadas

| # | Decisión |
|---|---|
| DA-1 | La continuación del chat no exige la tarea vigente del AsistenteLegal |
| DA-2 | A1: columna `IaProcesandoDesde`, con excepción a D14 |
| DA-3 | Extracción 30 s, lease 300 s (margen operacional), intervalo 60 s |
| DA-4 | PdfPig (Apache-2.0) y DocumentFormat.OpenXml (MIT) |
| DA-5 | XLSX incluido |
| DA-6 | 403 de almacenamiento sin código |
| DA-7 | El middleware emite el `ErrorCode` de las `BusinessRuleException` |
| DA-8 | `MetadatosJson` se conserva cuando falla una extracción |
| DA-9 | El resumen falla si un documento no se puede extraer |
| DA-10 | Se audita `AI_EXTRACTION_COMPLETED` |
| DA-11 | Se adaptan las pruebas de la Fase 6 afectadas |
| DA-12 | Códigos nuevos: `DOCUMENT_DOCUMENTS_INVALID`, `DOCUMENT_TEXT_EMPTY`, `DOCUMENT_TEXT_UNSUPPORTED`, `DOCUMENT_TEXT_INVALID`, `DOCUMENT_TEXT_EXTRACTION_FAILED` |
| DA-13 | **Variante A:** `SummarizeExpedienteAsync` (y los demás flujos de resumen y borrador) no modifica `EstadoIa` ni `MetadatosJson`; D8 es exclusiva de `ExtractFromDocumentAsync` y del worker de recuperación |
