# FASE 8.4 — CONTRATO TÉCNICO v1.1

| Campo | Valor |
|---|---|
| Versión | 1.1 |
| Estado | **DESIGN ONLY — v1.1 FINAL, READY FOR REVIEW. Implementación NO autorizada.** |
| Fecha | 2026-10-04 |
| Base | commit `7a4c6cd7c3fff4b38abef1bfac5537d6f42e72a9` (Fase 8.3 publicada) |
| Contrato rector | `FASE_8_CONTRATO.md` v1.1. En caso de conflicto prevalece, salvo en las decisiones cerradas de §30 que lo precisan de forma explícita |
| Documentos relacionados | `FASE_8_2_CONTRATO.md` v1.1 (extracción, normalización, fragmentación), `FASE_8_3_CONTRATO.md` v1.2 (proveedor de embeddings), `FASE_6X_CONTRATO.md` (patrón de worker y lease) |

Subfase del contrato rector (§21):

> **8.4 Indexación.** Worker: sembrado, reclamación con `SKIP LOCKED`, proceso, confirmación atómica, latido y lease, reintentos y backoff, purga, recuperación, tope diario, auditoría, `AIUsageLog`. Exclusiones: búsqueda. Migraciones: 0. Depende de: 8.2 y 8.3.

### Cambios v1.0 → v1.1

La v1.0 se entregó como propuesta en la revisión, sin archivo. La v1.1 conserva todo lo válido de ella y cierra las decisiones pendientes:

| # | Cambio | Secciones |
|---|---|---|
| 1 | **K-1:** la regla de "error interno no reintentable" se limita a las excepciones de `EmbedAsync` y a lo no previsto; los errores transitorios de la base y los estados tipados de la extracción conservan su propia política; la cancelación del host no es un error | §13, §23 |
| 2 | **K-2 y DP-2:** se mantienen los seis estados de la 8.1; una fila es una versión; sin columna `Generacion` | §6, §7 |
| 3 | **DP-1, DP-7 y K-4:** **0 migraciones**. No se toca `AIUsageLog`. Si el proveedor no informa tokens, no se escribe un 0 como consumo real: se documenta la limitación | §22 |
| 4 | **DP-3:** los campos de tokens de `AIUsageLog` contienen solo consumo informado por el proveedor; las estimaciones locales solo sirven para el presupuesto preventivo y la planificación interna, y nunca se mezclan con el consumo | §22, §26 |
| 5 | **DP-4:** coste `NULL` | §22 |
| 6 | **DP-5:** catálogo **cerrado** de códigos de error | §13 |
| 7 | **DP-6:** hash distinto → `Fallido` con `DOCUMENT_TEXT_INVALID`, evento de seguridad, sin reencolar | §14 |
| 8 | **DP-8:** parada limpia libera el trabajo (`Procesando → Pendiente`) sin sumar intento; la caída sigue recuperándose por lease con `Intentos + 1` | §11, §23 |
| 9 | **DP-9 y DP-10:** sin índices nuevos; sin endpoints | §5, §3 |
| 10 | Invariante explícito de atomicidad: nunca `N = Obsoleto` con `N+1 = Fallido` | §20 |
| 11 | Memoria: 12 MB es solo el tamaño bruto de los vectores; el consumo real se mide | §26 |
| 12 | Pruebas contractuales añadidas (errores, parada, activación, tokens, hash, lote) | §28 |

**Ajustes finales de la v1.1** (segunda revisión; no abren decisiones nuevas ni cambian estados, esquema, lotes, leases, reintentos, atomicidad, aislamiento ni catálogo de errores):

| # | Ajuste | Secciones |
|---|---|---|
| F1 | DP-3 sin ambigüedad: qué contiene cada campo de tokens y qué significa `documento_indices.TokensTotales` | §5, §18, §22.1, §26.2, §30 |
| F2 | DP-7: `AIUsageLog` no es la fuente única ni obligatoria de trazabilidad de un intento; la ausencia de la fila no significa que el intento no ocurrió | §22.3, §22.4 |
| F3 | Sección propia con las desviaciones intencionales y aprobadas respecto del contrato rector | §31 |

---

## 1. Objetivo

Convertir cada documento activo de un tenant con la indexación habilitada en un **índice semántico completo, vigente y atómico**:
- sus fragmentos (8.2) con sus embeddings (8.3) quedan persistidos en `documento_fragmentos`;
- quedan asociados a una fila de `documento_indices` en estado `Indexado`;
- ningún índice parcial es visible en ningún momento;
- una versión vigente nunca se pierde por el fallo de una versión nueva.

## 2. Alcance

1. Worker `IndexacionSemanticaBackgroundService`: sembrado, marcado, recuperación, adquisición, proceso, confirmación y purga.
2. Servicio de indexación de un documento: extracción → fragmentación → embeddings por lotes → confirmación atómica.
3. Lease con latido, recuperación de trabajos abandonados, liberación en parada limpia.
4. Reintentos del trabajo con backoff.
5. Idempotencia y activación atómica; retirada de la versión anterior.
6. Reacción al borrado lógico de documento o expediente y a la desactivación del tenant.
7. Presupuesto preventivo estimado por tenant y día.
8. Auditoría `DOCUMENT_INDEX*` y registro del consumo real en `AIUsageLog` con `Origen = Worker`.
9. Opciones `AI:Indexing` y su validación al arrancar.
10. Pruebas de §28.

## 3. Fuera de alcance

| Tema | Fase |
|---|---|
| Búsqueda vectorial y léxica, RRF, `BusquedaSemanticaRepository`, `POST /ai/buscar` | 8.5 |
| **Endpoints de reindexación manual, de activación del tenant y de estado y cobertura** (`AI.IndexManage`). La 8.4 solo procesa las filas `Pendiente` que existan (DP-10) | 8.5 |
| RAG, citas, `FuentesJson`, PSH | PSH y 8.6 |
| Índice ANN (HNSW), otra dimensión, proveedor autoalojado | Fuera de la Fase 8 |
| Cambios en `IEmbeddingProvider`, proveedores, `Fragmentador`, `TextoNormalizador` o el extractor | Ninguna: 8.2 y 8.3 están cerradas |
| Cambios de esquema: tablas, columnas, índices, `AIUsageLog` | Ninguna en la 8.4 (**0 migraciones**) |
| Precios y cálculo de `CostoEstimadoUsd` de embeddings | Evolución posterior (DP-4) |
| Angular | Fuera de la Fase 8 |

## 4. Arquitectura

```text
API            (sin cambios: ningún endpoint en la 8.4)

Application    IIndexacionSemanticaService     contrato del proceso de un documento y de los pasos del ciclo
               PerfilIndexacion                "{firma}|chunk-v1|ext-v1|norm-v1" (función pura)
               CodigosIndexacion               catálogo cerrado de §13

Infrastructure IndexacionSemanticaBackgroundService   bucle, ciclo, paralelismo, parada limpia
               IndexacionSemanticaService             sembrado, marcado, recuperación, adquisición, proceso,
                                                      latido, confirmación, fallo, liberación, purga
               IndexacionOptions (AI:Indexing) + validador

Usa sin modificar: IDocumentTextExtractor · Fragmentador · IEmbeddingProvider · FirmaEmbedding · LoteEmbeddings ·
                   IAuditService · ICurrentTenantService · ApplicationDbContext
Domain         (sin cambios)
```

- Es el patrón del worker de la 6.X (`ProcesamientoIaRecoveryBackgroundService`): `BackgroundService` con `Enabled`, un scope por tenant con `SetTenantId`, `CreateExecutionStrategy`, xmin y auditoría en la misma transacción. No hay colas externas.
- **El registro que representa el trabajo es la fila de `documento_indices`**: estado, lease, intentos y resultado viven ahí. No hay tabla de trabajos.
- **Perfil activo:** `FirmaEmbedding.De(proveedor) + "|chunk-v1|ext-v1|norm-v1"`, calculado al arrancar. Con el proveedor real: `openai-compatible:text-embedding-3-small@1536|chunk-v1|ext-v1|norm-v1`. Debe caber en `varchar(200)`; si no, la aplicación no arranca.
- Estado del repositorio verificado en `7a4c6cd7`: existen el modelo de la 8.1 (tablas, FK compuestas, únicos parciales, `vector(1536)`), el extractor y el fragmentador de la 8.2 y el proveedor de la 8.3. No existe ningún worker de indexación, ni escritura de fragmentos o índices, ni opciones `AI:Indexing`.

## 5. Modelo de datos

**Sin tablas, columnas ni índices nuevos. 0 migraciones** (DP-1, DP-2, DP-7, DP-9, K-4).

| Dato | Dónde vive (existente desde la 8.1) |
|---|---|
| Documento, tenant, expediente | `documento_indices` y `documento_fragmentos`: `TenantId`, `DocumentoId`, `ExpedienteId`, con FK compuestas |
| Versión del índice | La **fila** de `documento_indices` (§7) |
| Estado, lease, intentos | `Estado`, `ProcesandoDesde`, `ProcesadoPor`, `Intentos`, `ProximoIntentoEn`, `Version` (xmin) |
| Proveedor, modelo, dimensiones, firma del embedding y versiones de fragmentación, extracción y normalización | `Perfil` (contiene la firma y las tres versiones) y `Dimensiones` (`CHECK = 1536`) |
| Hash esperado del contenido | `HashContenido` |
| Fragmento y embedding | `documento_fragmentos`: `Orden`, `Texto`, `Ubicacion`, `RutaSeccion`, `CaracterInicio`, `CaracterFin`, `TokensEstimados`, `HashFragmento`, `Embedding vector(1536)` |
| Tamaño del índice | `Fragmentos` y `TokensTotales`. `TokensTotales` es una **estimación local del tamaño del texto indexado**; **no** es consumo del proveedor (§22.1) |
| Resultado de un fallo | `CodigoError`, `FragmentosCalculados`, `LimiteAplicado` |
| Fechas | `CreatedAt`, `UpdatedAt`, `IndexadoEn`, `ProcesandoDesde` |

**Restricciones e índices que usa la 8.4** (todos existentes):

| Uso | Restricción o índice |
|---|---|
| Como máximo una versión vigente por documento y perfil | `UX_documento_indices_Documento_Perfil_Vigente` (`WHERE Estado = 2`) |
| Como máximo una construcción en curso por documento y perfil | `UX_documento_indices_Documento_Perfil_EnCurso` (`WHERE Estado IN (0, 1)`) |
| Trabajos pendientes y leases vencidos | `IX_documento_indices_Estado_ProximoIntentoEn` |
| Índices vigentes de un expediente | `IX_documento_indices_Tenant_Expediente_Perfil_Indexado` |
| Un fragmento por posición | `UNIQUE (IndiceId, Orden)` |
| Fragmentos por expediente | `(TenantId, ExpedienteId, IndiceId)` |
| Aislamiento por tenant | FK compuestas `(TenantId, DocumentoId)`, `(TenantId, ExpedienteId)` y `(TenantId, IndiceId)` |
| Dimensión | `vector(1536)` y `CK_documento_indices_Dimensiones` |
| Formato de hashes, rangos y contadores | `CHECK` de la 8.1 |

**No se crean índices parciales por tenant** (DP-9): una optimización así queda condicionada a mediciones reales. El uso del GIN de `TextoBusqueda` y cualquier índice vectorial pertenecen a la 8.5 o posteriores.

## 6. Estados

Se mantienen **exactamente los seis estados** de `documento_indices` (K-2). No se añade ninguno para versiones, leases ni recuperación.

| Estado | Significado en la 8.4 |
|---|---|
| `Pendiente` (0) | En cola; elegible cuando llegue `ProximoIntentoEn` (o de inmediato si es `NULL`). Sin fragmentos |
| `Procesando` (1) | Adquirido por un worker, con lease vivo. Sin fragmentos |
| `Indexado` (2) | Completo, validado y vigente, con todos sus fragmentos |
| `Fallido` (3) | Error permanente o intentos agotados. Sin fragmentos. No se reintenta automáticamente |
| `Obsoleto` (4) | Fue vigente y lo reemplazó otra versión, o es de un perfil inactivo y nunca llegó a servir. Pendiente de purga |
| `PurgaPendiente` (5) | Documento o expediente borrado, o tenant desactivado. Pendiente de purga |

**Transiciones válidas:**

| De | A | Quién y cuándo | `Intentos` |
|---|---|---|---|
| (sin fila) | `Pendiente` | Sembrado | 0 |
| `Pendiente` | `Procesando` | Adquisición | Sin cambio |
| `Procesando` | `Indexado` | Confirmación atómica (§19) | Sin cambio |
| `Procesando` | `Pendiente` | Error transitorio del intento | **+1** |
| `Procesando` | `Pendiente` | Lease vencido (recuperación) | **+1** |
| `Procesando` | `Pendiente` | **Parada limpia** del host (§23) | **Sin cambio** |
| `Procesando` | `Pendiente` | Presupuesto preventivo agotado (§26) | **Sin cambio** |
| `Procesando` | `Fallido` | Error permanente, o `Intentos` alcanza el máximo | — |
| `Procesando` | `Obsoleto` | El `ExpedienteId` del documento ya no es el del índice | — |
| `Indexado` | `Obsoleto` | **Solo** dentro de la transacción que activa la versión nueva (§19) | — |
| `Pendiente`, `Fallido` | `Obsoleto` | Fila de un perfil que ya no es el activo | — |
| cualquiera | `PurgaPendiente` | Documento o expediente borrado; tenant desactivado | — |
| `Obsoleto`, `PurgaPendiente` | (fila borrada) | Purga; los fragmentos se van en cascada | — |
| `Fallido` | `Pendiente` | Solo la reactivación de `DOCUMENT_INDEX_TOO_LARGE` al subir el límite (rector §7.1). La reindexación manual es de la 8.5 | Reinicio a 0 |

**Transiciones inválidas** (ningún código las ejecuta; las pruebas lo verifican):
- `Indexado → Pendiente`, `Indexado → Procesando`, `Indexado → Fallido`;
- `Fallido → Procesando`;
- `Obsoleto` o `PurgaPendiente` hacia cualquier estado vivo;
- `Indexado → Obsoleto` fuera de la transacción de activación de su reemplazo;
- cualquier escritura del worker sobre una fila cuyo xmin no sea el suyo.

## 7. Versionado

- **Una fila de `documento_indices` representa una versión de indexación** (DP-2). Su identidad es la propia fila (`Id`) con sus relaciones (`TenantId`, `DocumentoId`, `Perfil`).
- **No hay columna `Generacion` ni contador separado.** El orden entre versiones lo da `CreatedAt`; la vigencia, el estado.
- Garantías, todas respaldadas por la base:
  - **una** versión activa por documento y perfil (único parcial `Vigente`);
  - **como máximo una** versión en progreso por documento y perfil (único parcial `EnCurso`);
  - las versiones anteriores quedan `Obsoleto` y se purgan;
  - una versión nueva **no destruye** la anterior hasta estar completamente construida, validada y activada, en la misma transacción.
- **Cuándo nace una versión:** sembrado (documento sin fila viva del perfil activo), cambio de perfil activo, o una fila `Pendiente` creada por la reindexación manual de la 8.5.
- **Cambio de perfil activo:** la versión `Indexado` del perfil anterior **no se marca `Obsoleto` de antemano**. Se retira en la transacción que activa la del perfil nuevo (rector §3.4: "se purgan cuando su documento ya tiene el nuevo"). Las filas `Pendiente` y `Fallido` del perfil anterior, que nunca sirvieron, sí pasan a `Obsoleto` en el marcado.

## 8. Idempotencia

**Mecanismos** (existentes):
- únicos parciales `EnCurso` y `Vigente`;
- `UNIQUE (IndiceId, Orden)`;
- `FOR UPDATE SKIP LOCKED` en la adquisición;
- lease (`Estado = Procesando`, `ProcesandoDesde`);
- xmin en **cada** escritura del worker;
- los fragmentos solo se escriben en la transacción de confirmación.

| Escenario | Resultado |
|---|---|
| Sembrado repetido o desde dos instancias | `ON CONFLICT DO NOTHING` más la condición "sin fila del perfil activo en `Pendiente`, `Procesando`, `Indexado` o `Fallido`": no se crea nada. Además, el sembrado lo ejecuta una sola instancia por ciclo (advisory lock) |
| Mismo documento solicitado varias veces | Una sola fila en curso; el único parcial rechaza la segunda |
| Dos workers ante el mismo trabajo | Uno lo adquiere; el otro lo salta. Un trabajo **no puede** procesarse a la vez por dos workers: solo se adquiere desde `Pendiente` |
| Reintento tras un fallo transitorio | El mismo índice vuelve a `Pendiente`; el intento anterior no dejó fragmentos |
| Caída tras generar embeddings | Estaban en memoria: se pierden. El lease vence y se reconstruye entero |
| Caída durante la confirmación | PostgreSQL revierte la transacción: ni fragmentos ni activación |
| Caída "después de persistir parcialmente" | **No existe persistencia parcial:** insertar los fragmentos y activar es una sola transacción |
| Worker recuperado que sigue vivo | Su xmin ya no coincide: latido y confirmación afectan 0 filas y abandona |
| Dos activaciones del mismo índice | La segunda no encuentra su xmin; el único parcial `Vigente` es la última barrera |

**Coste aceptado:** un reintento vuelve a pagar los embeddings del documento. Guardar lotes parciales exigiría fragmentos visibles o una tabla temporal, contra el "todo o nada" del contrato rector.

## 9. Worker

**Ciclo** cada `AI:Indexing:IntervalSeconds = 15` (rector §8). Cada paso corre en su propia transacción corta, dentro de `CreateExecutionStrategy().ExecuteAsync`:

| # | Paso | Detalle |
|---|---|---|
| 0 | Tenants habilitados | Se leen los tenants activos y se evalúa `ConfiguracionTenantIa.IndexacionSemanticaHabilitada` **en la aplicación**. Es la única fuente de verdad: `ConfiguracionJson` es texto y puede contener JSON inválido |
| 1 | Sembrado | Por tenant habilitado: documentos activos, de expediente activo, con `ContentType` soportado por el extractor y sin fila viva del perfil activo. `HashContenido` = `Documento.HashSha256` (puede ser `NULL` en documentos históricos). Lote de 500 por ciclo. Una sola instancia por ciclo (`pg_try_advisory_xact_lock` con clave propia) |
| 2 | Marcado | `PurgaPendiente` para índices de documentos o expedientes borrados y de tenants desactivados; `Obsoleto` para filas `Pendiente` o `Fallido` de perfiles inactivos (§7). Reactivación de `DOCUMENT_INDEX_TOO_LARGE` cuando `FragmentosCalculados` ya no supera el límite |
| 3 | Recuperación | Índices `Procesando` con el lease vencido (§11) |
| 4 | Adquisición | §10, hasta completar los huecos libres de `MaxParalelismo` |
| 5 | Proceso | Por documento, **fuera de cualquier transacción** |
| 6 | Purga | Hasta 200 índices `Obsoleto` o `PurgaPendiente` por ciclo, con auditoría `DOCUMENT_INDEX_PURGED` (§24) |

**Proceso de un documento** (scope propio con `SetTenantId` del tenant del índice; identificador de ejecución propio, §27):

1. Carga del documento con el filtro global de tenant. Si no está activo → `PurgaPendiente`.
2. Extracción (§14) y primer latido.
3. Comprobación del hash (§14).
4. Fragmentación (§15) y comprobación del límite de fragmentos.
5. Presupuesto preventivo estimado (§26).
6. Embeddings por lotes secuenciales (§16, §17), con un latido tras cada lote.
7. Validación del conjunto y confirmación atómica (§18, §19).

**Hosts de prueba:** `AI__Indexing__Enabled=false` en `TestHostDefaults`, como el worker de la 6.X. Las pruebas crean su propia instancia y ejecutan los pasos de forma explícita.

## 10. Lease

| Aspecto | Regla |
|---|---|
| **Adquisición** | Una transacción corta: se seleccionan las filas `Pendiente` del perfil activo, de tenants habilitados, con `ProximoIntentoEn` nulo o vencido, ordenadas por `ProximoIntentoEn` (nulos primero) y `CreatedAt`, con **`FOR UPDATE SKIP LOCKED`** y límite igual a los huecos libres; en la misma sentencia pasan a `Procesando` con `ProcesandoDesde = now()` y `ProcesadoPor`. Se devuelve el xmin |
| **Identidad del worker** | `ProcesadoPor = "{máquina}:{pid}:{id de instancia}"` (≤ 100 caracteres). Es **solo diagnóstico**: la propiedad del trabajo la prueba el xmin devuelto |
| **Qué protege `SKIP LOCKED`** | Solo la adquisición: dos instancias no eligen la misma fila. El bloqueo se libera al confirmar esa transacción; después protege el lease (rector §8) |
| **Duración** | **300 s desde el último latido** (rector §7). Supera cada paso individual: extracción (120 s) y un lote (60 s). Se valida al arrancar: `LeaseSeconds` mayor que el máximo de esos dos tiempos más un margen |
| **Latido** | Tras la extracción y tras cada lote: se renueva `ProcesandoDesde` exigiendo `Id`, `xmin = el propio` y `Estado = Procesando`. El worker reemplaza su xmin por el nuevo |
| **El latido comprueba además** | Que el tenant sigue habilitado y que el documento y el expediente siguen activos |
| **Latido con 0 filas** | El worker perdió el trabajo: **abandona sin escribir nada** |
| **Tope por tenant** | Como máximo 2 índices `Procesando` por tenant (rector §8). Es un límite **aproximado**: dos instancias pueden superarlo en una unidad en una carrera; afecta a la equidad, no a la corrección |
| **Reloj** | Toda la hora del lease es la de la base (`now()`), no la de cada instancia |

## 11. Recovery

| Situación | Detección | Resultado | `Intentos` |
|---|---|---|---|
| **Parada limpia** del host | El token de parada | El worker libera el trabajo: `Procesando → Pendiente` (§23) | **Sin cambio** |
| **Caída abrupta** (proceso muerto, contenedor reiniciado) | Nadie renueva el lease | El lease vence; la recuperación lo devuelve a `Pendiente` | **+1** |
| **Timeout de lease** con el worker vivo (operación más lenta que el lease) | Igual que la caída | Recuperación; el worker original abandona en su siguiente latido o confirmación (0 filas) | **+1** |
| **Error funcional** | Excepción o estado tipado durante el proceso | §12 y §13 | +1 si es transitorio |

**Paso de recuperación** (cualquier instancia, en cada ciclo):
1. Selecciona, con `FOR UPDATE SKIP LOCKED`, los índices con `Estado = Procesando` y `ProcesandoDesde` anterior a `now()` menos el lease.
2. Por cada uno, en un scope del tenant y exigiendo su xmin:
   - si `Intentos + 1` es menor que el máximo → `Pendiente`, `Intentos + 1`, lease limpio, `ProximoIntentoEn` con el backoff de §12; auditoría `DOCUMENT_INDEX_RECOVERED` (`motivo = LEASE_EXPIRED`) en la misma transacción;
   - si alcanza el máximo → `Fallido` con `CodigoError = LEASE_EXPIRED`; auditoría `DOCUMENT_INDEX_FAILED`.
3. Una fila ya recuperada por otra instancia no coincide en xmin y se omite.

**No hay nada que limpiar tras una caída:** un índice que no llegó a confirmarse no tiene fragmentos.

## 12. Retry

Dos niveles que **no se mezclan**:

| Nivel | Dueño | Política |
|---|---|---|
| **Proveedor** (dentro de una llamada a `EmbedAsync`) | 8.3 | Como máximo 3 intentos, base de 1,5 s, solo 429, 503 y red; 60 s por lote |
| **Base de datos** (dentro de una transacción del worker) | Estrategia de ejecución de EF ya configurada (`EnableRetryOnFailure`) | Reintenta la transacción completa ante errores transitorios de PostgreSQL |
| **Trabajo** (el índice entero) | 8.4 | Como máximo **5** intentos; backoff `min(2^Intentos × 1 min, 6 h)` con jitter de ±20 % (rector §7) |

Reglas del nivel de trabajo:
- La 8.4 **no envuelve `EmbedAsync` en otro bucle**. Un fallo transitorio de un lote **termina el intento**: el índice vuelve a `Pendiente` con `Intentos + 1` y `ProximoIntentoEn`.
- **429 con `EsperaSugerida`:** `ProximoIntentoEn = now() + max(backoff, EsperaSugerida)`, con tope de 6 h.
- **Agotados los 5 intentos** → `Fallido`, con el código del último fallo. Requiere intervención (reindexación manual, 8.5).
- **No suman intento:** la parada limpia, el aplazamiento por presupuesto y el abandono por pérdida del lease (ese ya lo cuenta la recuperación).
- **Contra tormentas de reintentos:**
  - backoff exponencial con jitter;
  - `MaxParalelismo` por instancia y tope por tenant;
  - **enfriamiento por instancia:** tras un fallo transitorio del proveedor, la instancia no adquiere trabajo nuevo durante 60 s (estado en memoria; sin tabla).

## 13. Clasificación de errores

### 13.1 Reglas por origen (K-1)

| Origen | Regla |
|---|---|
| **`EmbedAsync`** — la excepción implementa `IFalloProveedorEmbeddings` | Se clasifica **solo** por `EsTransitorio`. Transitorio → reintento del trabajo. Permanente → `Fallido` sin reintento |
| **`EmbedAsync`** — la excepción **no** implementa `IFalloProveedorEmbeddings` (incluida `ArgumentException` por lote inválido) | **Error interno no reintentable.** No se convierte en una excepción de proveedor ni se trata como transitoria. `Fallido` con `INDEX_INTERNAL_ERROR` |
| **Persistencia (PostgreSQL)** | Tiene su propia política: la estrategia de EF reintenta los errores transitorios. No contradice la regla anterior porque no proviene de `EmbedAsync`. Si el error persiste tras esos reintentos, el worker **no escribe ningún estado** (la base no es fiable en ese momento) y abandona: el lease vence y la recuperación cuenta el intento |
| **Conflicto de xmin** | No es un error ni se reintenta: otro actor tomó la fila. Se abandona sin escribir |
| **Extracción (8.2)** | Los estados tipados conservan su clasificación contractual (§14) |
| **Fragmentación (8.2)** | `SinTexto` y `LimiteSuperado` son permanentes (§15) |
| **Cancelación del host** | No es un error: no suma intento, no genera reintento y no se registra como fallo funcional (§23) |
| **Cualquier otra excepción no prevista** del proceso (fuera de `EmbedAsync` y de la base) | Error interno no reintentable: `Fallido` con `INDEX_INTERNAL_ERROR` |

### 13.2 Catálogo cerrado de `CodigoError` (DP-5)

La implementación **no puede** escribir en `documento_indices.CodigoError`, ni en `AIUsageLog.CodigoError` para la indexación, un valor que no esté en esta tabla. Una prueba lo verifica.

| Código | Cuándo | Tipo |
|---|---|---|
| `AI_PROVIDER_TIMEOUT` | `EmbeddingProviderTimeoutException` | Transitorio |
| `AI_PROVIDER_ERROR` | `EmbeddingProviderException`: transitorio (`LimiteDeTasa`, `NoDisponible`, `ErrorDelServidor`) o permanente (`Autenticacion`, `SolicitudRechazada`, `RespuestaInvalida`, `DimensionInvalida`, `ModeloInesperado`) | Según `EsTransitorio` |
| `DOCUMENT_TEXT_EXTRACTION_FAILED` | Extracción `ExtractionFailed` (timeout o E/S) | Transitorio |
| `DOCUMENT_TEXT_UNSUPPORTED` | Extracción `UnsupportedFormat` | Permanente |
| `DOCUMENT_TEXT_EMPTY` | Extracción `Empty`; fragmentación `SinTexto` | Permanente |
| `DOCUMENT_TEXT_INVALID` | Extracción `InvalidContent`; **hash distinto del esperado** (§14) | Permanente |
| `DOCUMENT_FILE_NOT_FOUND` | Extracción `FileNotFound` | Permanente |
| `DOCUMENT_INDEX_TOO_LARGE` | Fragmentación `LimiteSuperado` (con `FragmentosCalculados` y `LimiteAplicado`); extracción `ContextExceeded`, más de 3.000.000 de caracteres (ambos contadores `NULL`) | Permanente |
| `LEASE_EXPIRED` | La recuperación agota los intentos | Terminal |
| `INDEX_INTERNAL_ERROR` | Excepción no tipada de `EmbedAsync`; excepción no prevista del proceso; extracción `Forbidden` (ruta de almacenamiento rechazada) | Permanente |

- Son **códigos internos del índice**: la 8.4 no los expone por HTTP (no hay endpoints). `INDEX_INTERNAL_ERROR` y `LEASE_EXPIRED` son los dos únicos que no existían como códigos; `LEASE_EXPIRED` ya existía como motivo en la 6.X.
- En `CodigoError` va **solo el código**, nunca el mensaje de la excepción.
- `DOCUMENT_INDEX_TOO_LARGE` por `ContextExceeded` no se reactiva al subir el límite de fragmentos: esa regla exige `FragmentosCalculados`.
- La extracción `Forbidden` produce además un log de seguridad (ya lo emite el extractor).

### 13.3 Motivo en la auditoría (lista cerrada)

`DOCUMENT_INDEX_FAILED` lleva, además del código, un `motivo` de esta lista cerrada: el nombre de `MotivoFalloEmbedding` para los fallos del proveedor, o `HASH_MISMATCH`, `STORAGE_FORBIDDEN`, `CONTEXT_EXCEEDED`, `FRAGMENT_LIMIT`, `LEASE_EXPIRED`, `RETRIES_EXHAUSTED`, `INTERNAL`.

## 14. Extracción

- `ExtractSegmentsAsync(documentoId, ruta, contentType, ExtractionProfile.Indexacion, ct)`: 3.000.000 de caracteres y 120 s, constantes de `ext-v1`.
- La ruta y el `ContentType` salen del `Documento` cargado en el scope del tenant.
- Clasificación de los estados:

| Estado | Resultado | Código |
|---|---|---|
| `Success` | Continúa | — |
| `UnsupportedFormat` | `Fallido` | `DOCUMENT_TEXT_UNSUPPORTED` |
| `Empty` | `Fallido` | `DOCUMENT_TEXT_EMPTY` |
| `InvalidContent` | `Fallido` | `DOCUMENT_TEXT_INVALID` |
| `FileNotFound` | `Fallido` | `DOCUMENT_FILE_NOT_FOUND` |
| `Forbidden` | `Fallido` + log de seguridad | `INDEX_INTERNAL_ERROR` |
| `ContextExceeded` | `Fallido` | `DOCUMENT_INDEX_TOO_LARGE` |
| `ExtractionFailed` | **Transitorio:** `Pendiente` con backoff; `Fallido` al agotar | `DOCUMENT_TEXT_EXTRACTION_FAILED` |

**Documento inmutable y hash esperado (DP-6):**
- El contenido de un documento es inmutable tras la subida (rector §1 y §16). El PUT de metadatos no reindexa.
- El testigo de cambio de contenido es el **hash del archivo**, no `Documento.Version`: el xmin del documento cambia con cualquier PUT de metadatos o cambio de `EstadoIa`.
- **Hash esperado:** `Documento.HashSha256`. En documentos históricos sin hash, el primer `HashSha256Archivo` extraído pasa a ser `HashContenido` del índice.
- Comprobaciones:
  1. tras la extracción: si `Documento.HashSha256` existe y difiere de `HashSha256Archivo`;
  2. en la confirmación: si `Documento.HashSha256` ya no coincide con el `HashContenido` del índice.
- **Si no coincide:** `Fallido` con `DOCUMENT_TEXT_INVALID`.
  - Se registra un **evento de seguridad**: log de nivel `Error` y auditoría `DOCUMENT_INDEX_FAILED` con `motivo = HASH_MISMATCH`.
  - **No se reencola** ni se usa `Obsoleto`.
  - **No hay bucle:** el sembrado no crea otra fila mientras exista la `Fallido` del perfil activo.

## 15. Fragmentación

- `Fragmentador.Fragmentar(segmentos, AI:Indexing:MaxFragmentosPorDocumento, ct)` con `chunk-v1` y `norm-v1`, sin cambios (`target 1500`, `max 2000`, `overlap 200`, `min 200`).
- Resultados:
  - `Fragmentado` → continúa;
  - `SinTexto` → `Fallido` con `DOCUMENT_TEXT_EMPTY`;
  - `LimiteSuperado` → `Fallido` con `DOCUMENT_INDEX_TOO_LARGE`, `FragmentosCalculados` y `LimiteAplicado`, y auditoría (rector §7.1).
- La comprobación ocurre **antes** de cualquier llamada al proveedor: un documento que supera el límite no consume embeddings.
- Si había una versión vigente, **sigue vigente**.

## 16. Embeddings

- `IEmbeddingProvider.EmbedAsync(textos, EmbeddingPurpose.Documento, ct)`, sin modificar la 8.3.
- Entrada: **solo** `FragmentoPreparado.Texto`. Nunca identificadores, títulos, rutas ni hashes.
- Cada lote contiene fragmentos de **un solo documento** de **un solo tenant**.
- Modelo `text-embedding-3-small`, 1536 dimensiones. La 8.4 no pide otra dimensión, no rellena, no trunca y no convierte.
- Los vectores se guardan en memoria hasta la confirmación, sin alterarlos.
- Si `resultado.Dimensiones`, `resultado.ModelId` o `resultado.ProviderId` no coinciden con los del perfil con el que se adquirió el trabajo, es un error interno (`INDEX_INTERNAL_ERROR`): el proveedor cambió bajo el worker.

## 17. Batch

- **Tamaño efectivo del lote: `min(proveedor.MaxEntradasPorLote, 64)`.**
- 64 es el máximo contractual absoluto de la 8.3 (`LoteEmbeddings.MaxEntradasPorLoteContractual`). No se supera aunque la configuración indique un número mayor; además, la validación de la 8.3 rechaza cualquier lote de más de 64.
- La 8.4 **no tiene una opción propia de tamaño de lote**: usa la del proveedor.
- Los lotes de un documento son **secuenciales** y conservan el orden: el lote `k` cubre los fragmentos `k × tamaño` a `(k + 1) × tamaño − 1`.
- Por cada lote se valida, antes de aceptarlo: `Vectores.Count` igual al número de textos enviados y 1536 valores por vector.
- Tras cada lote: latido (§10).

## 18. Persistencia

**Condiciones previas a la activación** (todas obligatorias):
1. la extracción terminó con `Success`;
2. la normalización y la fragmentación terminaron con `Fragmentado`;
3. se generaron los embeddings de **todos** los fragmentos;
4. **todos** los lotes se validaron;
5. el conjunto es coherente: `vectores.Count == fragmentos.Count`, cada vector de 1536 valores.

**Escritura** (solo en la transacción de confirmación):
- Cada `FragmentoPreparado` se copia campo a campo. `Ubicacion` se serializa a `jsonb` como `{ "tipo", "desde", "hasta", "etiqueta" }`.
- `TenantId`, `DocumentoId`, `ExpedienteId` e `IndiceId` se toman **de la fila del índice**, nunca de una entrada externa.
- `Embedding` es el vector del proveedor **tal cual**.
- En el índice: `Fragmentos = n`, `TokensTotales = Σ TokensEstimados` de sus fragmentos (estimación local del tamaño, **no** consumo del proveedor; §22.1), `IndexadoEn`, `HashContenido`, `CodigoError = NULL`, lease limpio.

No existe ninguna ruta que escriba fragmentos de un índice que no vaya a quedar `Indexado` en esa misma transacción.

## 19. Activación

Una transacción dentro de `CreateExecutionStrategy().ExecuteAsync`, en este orden:

1. **Propiedad.** Se actualiza la fila propia exigiendo `Id`, `xmin = el propio` y `Estado = Procesando`. Con 0 filas, se abandona. Este paso toma el bloqueo de la fila antes de escribir nada más.
2. **Revalidación con bloqueo.** Se leen el documento y el expediente con `FOR SHARE`: un borrado lógico concurrente espera a esta transacción, o ya está confirmado y se ve.
   - Documento o expediente borrado, o tenant desactivado → se descarta; índice a `PurgaPendiente`.
   - `ExpedienteId` distinto del índice → se descarta; índice a `Obsoleto`.
   - Hash distinto → se descarta; índice a `Fallido` con `DOCUMENT_TEXT_INVALID` (§14).
3. **Inserción** de todos los fragmentos.
4. **Retirada de la versión vigente anterior** del mismo documento (de cualquier perfil): pasa a `Obsoleto`. Va **antes** del paso 5, porque el único parcial `Vigente` se comprueba en cada sentencia.
5. **Activación:** la fila propia pasa a `Indexado`.
6. Auditoría `DOCUMENT_INDEXED` y, si procede, `AIUsageLog` del intento (§22).
7. Confirmación de la transacción.

Los descartes del paso 2 se escriben en una transacción propia posterior, tras revertir esta: nunca conviven con fragmentos insertados.

## 20. Atomicidad

**Invariante.** Para un documento con una versión vigente `N` y una versión `N+1` en construcción:

| Momento | Estado confirmado en la base |
|---|---|
| Antes de activar `N+1` | `N = Indexado` · `N+1 = Procesando` |
| Activación con éxito (una única transacción) | `N = Obsoleto` · `N+1 = Indexado` |
| Fallo en cualquier punto de la construcción o de la activación | `N = Indexado` · `N+1 = Fallido` (o `Pendiente`, si el fallo es transitorio) |

> **Nunca existe un estado confirmado con `N = Obsoleto` y `N+1 = Fallido`.** Una versión vigente no puede perderse por el fallo de la versión nueva.

**Por qué se cumple:**
- `N → Obsoleto` solo ocurre **dentro** de la transacción que hace `N+1 → Indexado` (§19, pasos 4 y 5). Si esa transacción falla, PostgreSQL revierte los dos cambios y también los fragmentos.
- Marcar `N+1` como `Fallido` o `Pendiente` es **otra** transacción, que solo toca la fila de `N+1`.
- Ningún otro paso del worker pasa a `Obsoleto` una fila `Indexado` (§6 y §7): el marcado solo afecta a filas `Pendiente` o `Fallido`.
- Con el aislamiento por defecto (`READ COMMITTED`), ninguna otra transacción ve el índice nuevo sin sus fragmentos ni dos índices vigentes a la vez: el cambio se hace visible entero al confirmar.

## 21. Multi-tenant

El aislamiento **no depende solo de filtros de aplicación**:

| Capa | Garantía |
|---|---|
| **Base de datos** | FK compuestas `(TenantId, DocumentoId) → documentos`, `(TenantId, ExpedienteId) → expedientes` y `(TenantId, IndiceId) → documento_indices`: PostgreSQL rechaza un índice o un fragmento que apunte a un documento, expediente o índice de otro tenant |
| **Identificadores** | Los `TenantId`, `DocumentoId`, `ExpedienteId` e `IndiceId` de los fragmentos se copian de la fila del índice |
| **Scopes** | Cada documento se procesa en un scope con `SetTenantId` del tenant del índice; el documento se carga con el filtro global |
| **Consultas globales** | El listado de tenants habilitados, la adquisición y la recuperación usan `IgnoreQueryFilters` con predicado explícito y solo leen identificadores, como el worker de la 6.X |
| **Transacciones** | La activación filtra la retirada por `DocumentoId` (único) y exige el xmin de la fila propia |
| **Proveedor** | No recibe ningún identificador; un lote nunca mezcla documentos ni tenants |

Queda impedido: fragmentos, embeddings o índices de otro tenant; documentos cruzados; y trabajos cruzados.

## 22. AIUsageLog

**El esquema de `AIUsageLog` no se modifica en la 8.4** (DP-1, DP-7): sin `TokensInformados`, sin columnas, sin FK al trabajo, sin tablas nuevas.

### 22.1 Consumo real frente a estimación local (DP-3)

Son dos magnitudes distintas, guardadas en **entidades distintas**, que nunca se suman, se comparan ni se sustituyen entre sí.

**Consumo real — solo en `AIUsageLog`:**
- `AIUsageLog.TokensEntrada`, `AIUsageLog.TokensSalida` y `AIUsageLog.TotalTokens` representan **únicamente consumo informado por el proveedor**.
- Para la indexación: `TokensEntrada` = suma de los `EmbeddingBatchResult.TokensEntrada` informados; `TokensSalida = 0`, porque un embedding no produce tokens de salida; `TotalTokens = TokensEntrada`.
- **Nunca** se introduce en `AIUsageLog` un token estimado, ni como relleno, ni como aproximación, ni para completar un lote que no informó su consumo.

**Estimación local — nunca en `AIUsageLog`:**
- Es `FragmentoPreparado.TokensEstimados` (convención caracteres / 4 de la 8.2), calculada sin llamar al proveedor.
- Solo se usa para **presupuesto preventivo y planificación interna** (§26.2).
- No se presenta como consumo en ningún registro, log, auditoría ni métrica: donde aparece, se etiqueta como estimación.

**`documento_indices.TokensTotales` — pertenece al índice, no a `AIUsageLog`:**
- Es una columna de otra entidad (`DocumentoIndice`, 8.1).
- **Significado en la 8.4:** estimación local del **tamaño del texto indexado** de esa versión: `Σ TokensEstimados` de sus fragmentos.
- **No representa consumo real del proveedor**, no se deriva de `usage` y no puede usarse para facturación, costes ni informes de consumo.
- Su único uso es como dato de tamaño del índice y como entrada del presupuesto preventivo estimado.

| Campo | Entidad | Contenido | ¿Consumo real del proveedor? |
|---|---|---|---|
| `TokensEntrada`, `TokensSalida`, `TotalTokens` | `AIUsageLog` | Tokens informados por el proveedor | **Sí, y solo eso** |
| `TokensTotales` | `DocumentoIndice` | Estimación local del tamaño del texto indexado | **No** |
| `TokensEstimados` | `DocumentoFragmento` | Estimación local del tamaño del fragmento (8.2) | **No** |

### 22.2 Registro

Un registro por **intento de indexación de un documento** que haya completado al menos un lote, y solo si el consumo es íntegramente real:

| Campo | Valor |
|---|---|
| `TenantId` | El del índice |
| `Origen` / `UsuarioId` / `ActorSistema` | `Worker` / `NULL` / `worker:indexacion-semantica` |
| `CasoUso` | `IndexacionSemantica` (6) |
| `ProviderId`, `ModelId` | Los del resultado del proveedor (modelo efectivo) |
| `TokensEntrada`, `TotalTokens` | Suma de los tokens **informados** de los lotes completados. `TokensSalida = 0` |
| `DuracionMs` | Suma de la duración de las llamadas al proveedor |
| `CostoEstimadoUsd` | **`NULL`** (DP-4): no hay un precio oficial y verificado; no se codifican precios de terceros ni históricos |
| `Exitoso` | `true` si el índice se confirmó; `false` en otro caso |
| `CodigoError` | Código del catálogo de §13.2; `NULL` en el éxito y en la parada limpia |

- **Éxito:** en la misma transacción de la confirmación.
- **Fallo, abandono o parada limpia con lotes ya completados:** en un contexto independiente, como en la 6.X.
- **Ningún lote completado:** no se registra nada; no hay consumo.

### 22.3 Limitación documentada: tokens no informados

`AIUsageLog.TokensEntrada` es `int NOT NULL` y **no puede representar "no informado"**. Por eso:
- Si **algún** lote del intento devuelve `TokensEntrada = null`, **no se escribe la fila de `AIUsageLog`** de ese intento. Escribir 0, o una suma parcial, lo presentaría como consumo real.
- No se inventan tokens ni se usa la estimación local en su lugar: una estimación nunca entra en `AIUsageLog`.
- **La ausencia de la fila de `AIUsageLog` no significa que el intento no haya ocurrido.** El intento sigue siendo trazable por las otras fuentes de §22.4, con `tokensInformados = null` y el número de lotes sin consumo informado.
- **Deuda técnica** para una evolución posterior: dar a `AIUsageLog` una forma de representar el consumo no informado. No se resuelve con una migración en la 8.4.
- Alcance práctico: el proveedor de OpenAI informa `usage` y el proveedor simulado informa siempre su consumo; la limitación solo afecta a servidores compatibles que no lo informan.

### 22.4 Trazabilidad por intento, sin cambiar el esquema (DP-7)

**`AIUsageLog` no es la fuente única ni obligatoria de la trazabilidad de un intento.** Es el registro del consumo real, y solo existe cuando hay consumo íntegramente informado (§22.2, §22.3). Que un intento no tenga fila en `AIUsageLog` **no significa que no haya ocurrido**.

Todo intento, tenga o no fila en `AIUsageLog`, es trazable por estas fuentes, sin columnas ni migraciones nuevas:

| Fuente | Qué aporta | ¿Existe siempre? |
|---|---|---|
| **`ejecucionId`** | Identificador de ejecución generado por el worker en cada intento; une todas las demás fuentes | Sí |
| **Logs estructurados** (§27) | Adquisición, cada lote, resultado del intento, reintento programado, liberación o descarte; con `ejecucionId`, índice, documento, tenant, intento, lotes completados y tokens informados o "no informado" | Sí, en todo intento |
| **Estado del índice** | `Estado`, `Intentos`, `ProximoIntentoEn`, `CodigoError` (código del último fallo; se limpia al confirmar), `ProcesadoPor`, `ProcesandoDesde`, `IndexadoEn`, `FragmentosCalculados`, `LimiteAplicado` | Sí |
| **Auditoría** (§27) | `DOCUMENT_INDEXED` y `DOCUMENT_INDEX_FAILED` en los resultados terminales; `DOCUMENT_INDEX_RECOVERED` en cada recuperación por lease; `DOCUMENT_INDEX_PURGED` en la purga. Con `ejecucionId`, `intento`, `lotesCompletados`, `tokensInformados` (o `null`) y `aiUsageLogId` (o `null`) | En los eventos que el contrato rector audita |
| **Información de recuperación y reintento** | `Intentos` y `ProximoIntentoEn` de la fila, el log `[INDEX_RETRY]` o `[INDEX_LEASE_EXPIRED]` y la auditoría `DOCUMENT_INDEX_RECOVERED` | Sí, cuando hay reintento o recuperación |
| **`AIUsageLog`** | El consumo real del intento: proveedor, modelo, tokens informados, duración, resultado y código | **Solo** si el intento completó al menos un lote y todos informaron su consumo |

Con ellas se reconstruye de cada intento: el tenant, el documento, el número de intento, la ejecución, el proveedor y el modelo, los lotes completados, los tokens reales informados (si existen), el resultado y el error.

- **Vínculo con `AIUsageLog`, cuando existe:** el evento de auditoría y el log del intento llevan `aiUsageLogId`, el `Id` de la fila escrita. Cuando no existe, ese campo es `null` y el resto de la traza está completo.
- **Eventos terminales:** quedan en la auditoría, que es durable.
- **Intentos no terminales** (fallo transitorio, parada limpia): el contrato rector no los audita (§17). Quedan trazados por el `ejecucionId`, el log estructurado del intento, el estado del índice (`Intentos`, `ProximoIntentoEn`, `CodigoError`) y, si llegó a haber consumo informado, la fila de `AIUsageLog`.

## 23. Shutdown

| Situación | Qué ocurre | `Intentos` | ¿Es un fallo? |
|---|---|---|---|
| **Parada limpia** (el host cancela el token) | El worker deja de procesar y **libera** cada trabajo propio: `Procesando → Pendiente`, lease limpio, `ProximoIntentoEn` sin cambio | **Sin cambio** | **No.** No consume reintento, no se audita como fallo y su log es informativo |
| **Caída abrupta** | Nadie libera; el lease vence; la recuperación lo devuelve a `Pendiente` | **+1** | Cuenta como intento |
| **Timeout de lease** con el worker vivo | Igual que la caída; el worker original abandona al ver 0 filas | **+1** | Cuenta como intento |
| **Error funcional** | §12 y §13 | +1 si es transitorio | Sí |

**Reglas de la parada limpia** (DP-8):
- La liberación exige el xmin propio y `Estado = Procesando`: si el trabajo ya no es del worker, no escribe nada.
- Se ejecuta con un token propio y un plazo corto, dentro del tiempo de apagado del host. Si no llega a completarse, el caso degrada al de caída abrupta (lease).
- La cancelación se propaga a la extracción, al proveedor y a la confirmación: nada se confirma a medias.
- Si ya se habían completado lotes con consumo real informado, ese consumo se registra en `AIUsageLog` con `Exitoso = false` y `CodigoError = NULL`: es un intento interrumpido, no un error.
- Esta regla precisa el contrato rector §15 ("vuelve a `Pendiente` por lease"): la parada limpia libera de inmediato, y el lease queda para las caídas (§30, DP-8).

## 24. Purga

- Si el documento o su expediente están borrados lógicamente, **no puede activarse ninguna versión nueva**: lo impiden el sembrado (exige documento activo), el latido y la revalidación con `FOR SHARE` de la confirmación.

| Momento del borrado | Comportamiento |
|---|---|
| Índice `Pendiente` | El marcado lo pasa a `PurgaPendiente`; nunca se adquiere |
| Índice `Procesando` | El latido lo detecta y abandona; si el borrado llega después, la confirmación lo ve y descarta. Queda `PurgaPendiente` |
| Índice `Indexado` | El marcado lo pasa a `PurgaPendiente` |
| Entre el borrado y la purga | La búsqueda (8.5) ya no lo devuelve: exige documento y expediente activos |

- La purga **borra físicamente** la fila de `documento_indices`; los fragmentos se van en cascada. Lote de 200 por ciclo.
- Auditoría `DOCUMENT_INDEX_PURGED` con `motivo`: `DOCUMENTO_ELIMINADO`, `PERFIL_OBSOLETO`, `TENANT_DESACTIVADO` (rector §17). `HASH_DISTINTO` no se usa: ese caso es `Fallido` (DP-6).
- `PurgaPendiente` no tiene salida salvo el borrado físico.
- **Tenant desactivado** (rector §3.2.2): se detiene el sembrado, los trabajos en curso abandonan en su siguiente latido, y sus índices pasan a `PurgaPendiente` y se purgan.

## 25. Seguridad

- **Consentimiento:** solo se indexan tenants con `ia.indexacionSemantica = true` (falso por defecto).
- **Autorización:** el worker no es un usuario y no expone nada; solo procesa documentos activos de expedientes activos del tenant del índice.
- **Proveedor externo:** sale solo el texto de los fragmentos (8.3). El contenido jurídico no se anonimiza (decisión de la 8.2).
- **Embeddings tan sensibles como el texto:** no se registran, no se exponen por API y no salen del tenant.
- **Logs y auditoría, prohibido:** texto de fragmentos, vectores, rutas de almacenamiento, títulos, nombres de hoja, claves, JWT, cookies y mensajes de excepción.
- **Carreras:** xmin en cada escritura, `FOR SHARE` en la confirmación y únicos parciales.
- **Repetición o reintento:** no duplica fragmentos ni índices (§8).
- **Integridad:** un hash distinto del esperado es un evento de seguridad (§14).
- **Borrado:** efectivo de inmediato para la búsqueda futura; purga física asíncrona.
- **Secretos:** la 8.4 no añade ninguno. `Mock` sigue prohibido en `Production` (8.3).

## 26. Performance

### 26.1 Límites operativos

| Límite | Valor | Fuente |
|---|---|---|
| Textos por lote | `min(proveedor.MaxEntradasPorLote, 64)` | 8.3 |
| Fragmentos por documento | 2.000 (`AI:Indexing:MaxFragmentosPorDocumento`) | Rector §7.1 |
| Caracteres por documento | 3.000.000 (`ext-v1`) | 8.2 |
| Documentos en proceso por instancia | `AI:Indexing:MaxParalelismo = 2` | Rector §8 |
| Documentos en proceso por tenant | 2 (aproximado) | Rector §8 |
| Peticiones simultáneas al proveedor | Como máximo `MaxParalelismo` por instancia: los lotes de un documento son secuenciales | Este contrato |
| Polling | 15 s | Rector §8 |
| Sembrado / purga por ciclo | 500 / 200 | Rector §8 |
| Lease | 300 s desde el último latido | Rector §7 |
| Intentos del trabajo | 5 | Rector §7 |
| Presupuesto preventivo por tenant y día | 5.000.000 de tokens **estimados** (`AI:Indexing:MaxTokensDiariosPorTenant`) | Rector §14 |

### 26.2 Presupuesto preventivo estimado

- Es un **límite preventivo basado en estimaciones**, no una medida de consumo real, y nunca se registra en `AIUsageLog`.
- **Medida:** suma de `documento_indices.TokensTotales` (estimación local del tamaño, no consumo; §22.1) de los índices del tenant con `IndexadoEn` dentro del día UTC en curso, más la estimación del documento candidato (`Σ TokensEstimados` de sus fragmentos). No interviene ningún dato de `AIUsageLog`.
- **Momento:** tras la fragmentación y antes de la primera llamada al proveedor.
- **Si no cabe:** el índice vuelve a `Pendiente` con `ProximoIntentoEn` al inicio del día UTC siguiente (con jitter); **no** suma intento ni pasa a `Fallido`.
- **Es aproximado por diseño:** no cuenta los intentos fallidos ni los índices ya purgados. Su función es frenar un volumen anómalo, no contabilizar.

### 26.3 Memoria

- `2.000 × 1536 × 4 bytes ≈ 12 MB` es **solo el tamaño bruto aproximado de los vectores `float32`** de un documento del tamaño máximo. **No** es la memoria del proceso ni un límite de RAM.
- La memoria real incluye además: objetos y arrays de .NET, las cadenas de los fragmentos y del texto extraído, los búferes HTTP, el JSON de las respuestas y su deserialización, las estructuras de EF y Npgsql durante la confirmación, las colecciones temporales, el logging y la sobrecarga del runtime; todo ello multiplicado por los documentos en paralelo.
- **El consumo real de memoria debe medirse con pruebas de rendimiento controladas.** Este contrato no fija ninguna cifra.

### 26.4 Benchmarks a realizar (sin cifras previas)

Este contrato **no afirma ningún número de rendimiento**: no existen mediciones. La implementación deberá medir y documentar, declarando el entorno (hardware, versiones, volumen):

| Medida | Escenario |
|---|---|
| Duración total por documento | Pequeño (1 fragmento), mediano, grande (2.000 fragmentos) |
| Duración por lote y por fase | Extracción, fragmentación, embeddings (con el proveedor simulado y, aparte, observacional con el real), confirmación |
| Persistencia | Inserción de 2.000 fragmentos con vectores de 1536 dimensiones, incluida la columna generada y su GIN |
| Memoria | Pico del proceso con 1 y con `MaxParalelismo` documentos de 2.000 fragmentos |
| Throughput | Documentos y fragmentos por minuto con concurrencia 1 y `MaxParalelismo` |
| Concurrencia | Dos instancias del worker sobre la misma cola |
| Recuperación | Tiempo desde la caída hasta la reanudación (lease) y coste del paso de recuperación con muchos índices |
| Adquisición | Coste de la consulta con decenas de miles de filas `Pendiente` (dato para DP-9) |

La latencia del proveedor externo se informa aparte, como dato observacional, y no se mezcla con el benchmark de búsqueda ni con el de RAG.

## 27. Observabilidad

**Logs** (etiqueta, nivel y campos; nunca contenido). Todos llevan `ejecucionId`, índice, documento y tenant:

| Evento | Etiqueta y nivel | Campos adicionales |
|---|---|---|
| Trabajo sembrado | `[INDEX_SEEDED]` Debug | Número de índices sembrados |
| Trabajo adquirido | `[INDEX_CLAIMED]` Information | Instancia, intento |
| Lease vencido | `[INDEX_LEASE_EXPIRED]` Warning | Intentos |
| Reintento programado | `[INDEX_RETRY]` Warning | Intento, código, motivo, próximo intento, lotes completados, `aiUsageLogId` |
| Lote iniciado y completado | `[INDEX_BATCH]` Debug | Número de lote, textos, duración, tokens informados o "no informado" |
| Lote fallido | `[INDEX_BATCH_FAILED]` Warning | Motivo, transitorio, estado HTTP, intentos del proveedor |
| Construcción iniciada y completada | `[INDEX_BUILD]` Information | Fragmentos, duración |
| Activación correcta | `[INDEX_ACTIVATED]` Information | Fragmentos, índice reemplazado |
| Activación descartada | `[INDEX_ACTIVATION_DISCARDED]` Warning | Causa: xmin, borrado, expediente, hash |
| Versión retirada | `[INDEX_SUPERSEDED]` Information | Índice retirado e índice nuevo |
| Trabajo liberado por parada | `[INDEX_RELEASED]` Information | Lotes completados |
| Aplazado por presupuesto | `[INDEX_BUDGET_DEFERRED]` Information | Estimación del documento, próximo intento |
| Hash distinto | `[INDEX_HASH_MISMATCH]` Error (seguridad) | — |
| Error interno | `[INDEX_INTERNAL_ERROR]` Error | Tipo de excepción |

**Auditoría** (rector §17), todas con `actor = worker:indexacion-semantica` y sin usuario:

| Evento | Cuándo | Contenido |
|---|---|---|
| `DOCUMENT_INDEXED` | Confirmación | `indiceId`, `ejecucionId`, `expedienteId`, `perfil`, `fragmentos`, `tokensTotales` (estimación local del tamaño del texto; no es consumo), `tokensInformados` (consumo real informado por el proveedor, o `null`), `lotesCompletados`, `intento`, `aiUsageLogId`, `indiceReemplazadoId` |
| `DOCUMENT_INDEX_FAILED` | Paso a `Fallido` | Lo anterior que aplique, más `codigoError`, `motivo`, `intentos` y, si procede, `fragmentosCalculados` y `limiteAplicado` |
| `DOCUMENT_INDEX_RECOVERED` | Lease vencido recuperado | `indiceId`, `expedienteId`, `perfil`, `motivo = LEASE_EXPIRED`, `intentos` |
| `DOCUMENT_INDEX_PURGED` | Purga | `indiceId`, `expedienteId`, `perfil`, `motivo` |

**Métricas** (definidas; su exposición se decide en la implementación): índices por estado, intentos por índice, leases vencidos, liberaciones por parada, latencia y fallos de lotes por motivo, fragmentos y documentos indexados, activaciones descartadas, tokens reales informados, intentos sin consumo informado, aplazamientos por presupuesto y antigüedad del `Pendiente` más viejo.

## 28. Tests

**U** = unitaria; **I** = integración con PostgreSQL real; **E2E** = host completo. Proveedor: `MockEmbeddingProvider` o un doble programable en el proyecto de pruebas. Las pruebas que escriben en `ai_usage_logs` usan la base temporal de la 8.1, para no dejar filas inmutables en la base de desarrollo.

| Área | Caso | Tipo | Esperado |
|---|---|---|---|
| **Worker** | Sembrado idempotente (dos ciclos, dos instancias) | I | Una fila por documento |
| | Tenant sin activación; formato no soportado; documento o expediente borrado | I | No se siembra |
| | Adquisición simultánea de dos instancias | I | Conjuntos disjuntos (`SKIP LOCKED`) |
| | Fila `Procesando` con lease vigente | I | No adquirible |
| | `MaxParalelismo` y tope por tenant | I | Respetados |
| **Lease y recovery** | El latido renueva el lease | I | No se recupera mientras late |
| | Lease vencido | I | `Pendiente`, `Intentos + 1`, auditoría `DOCUMENT_INDEX_RECOVERED` |
| | Worker que sigue vivo tras ser recuperado | I | Latido y confirmación con 0 filas; sin escritura |
| | Lease vencido con intentos agotados | I | `Fallido` con `LEASE_EXPIRED` |
| **Errores** | `IFalloProveedorEmbeddings` transitorio (429, 503, timeout, 5xx) | I | `Pendiente` con backoff; `Intentos + 1`; `Fallido` al 5.º |
| | `IFalloProveedorEmbeddings` permanente (autenticación, respuesta inválida, dimensión inválida, modelo inesperado) | I | `Fallido` inmediato; **sin reintento** |
| | **Excepción no tipada de `EmbedAsync`** | I | `Fallido` con `INDEX_INTERNAL_ERROR`; **sin reintento**; no se convierte en excepción de proveedor |
| | Error transitorio de la base en una transacción del worker | I | La estrategia de EF la repite; el índice termina correcto |
| | Error persistente de la base | I | Sin escritura de estado; recuperación por lease |
| | Extracción `ExtractionFailed` | I | Reintento según la 8.2; `Fallido` con `DOCUMENT_TEXT_EXTRACTION_FAILED` al agotar |
| | Resto de estados de extracción y de fragmentación | I | Código y tipo de §13 y §14 |
| | Cancelación del host | I | Sin reintento, sin `Intentos + 1`, sin fallo |
| | Catálogo cerrado | U e I | Ningún `CodigoError` fuera de §13.2 |
| | 429 con `EsperaSugerida` | U e I | `ProximoIntentoEn` ≥ la espera |
| | Backoff | U | `min(2^n × 1 min, 6 h)` ± 20 % |
| | Enfriamiento del proveedor | I | Sin adquisiciones durante 60 s |
| **Shutdown** | Parada limpia a mitad de un documento | I | `Procesando → Pendiente`; `Intentos` sin cambio; sin fragmentos; sin auditoría de fallo |
| | Parada limpia con lotes ya completados | I | Consumo real registrado con `Exitoso = false` y `CodigoError = NULL` |
| | Caída simulada y lease vencido | I | `Pendiente` con `Intentos + 1` |
| **Activación** | Éxito | I | En una transacción: `N → Obsoleto`, `N+1 → Indexado` |
| | Fallo de la versión nueva (antes, durante y en la confirmación) | I | `N` sigue `Indexado`; `N+1` queda `Fallido`; 0 fragmentos de `N+1` |
| | **Invariante** | I | En ningún momento existe un estado confirmado `N = Obsoleto` con `N+1 = Fallido` |
| | Lectura concurrente durante la activación | I | Nunca dos vigentes ni un vigente sin fragmentos |
| | Dos confirmaciones del mismo índice | I | Una gana; la otra se descarta sin escribir |
| | Cambio de perfil activo | I | El índice del perfil anterior sigue `Indexado` hasta que el nuevo se activa |
| **Idempotencia** | Mismo documento sembrado o solicitado dos veces | I | Una sola construcción |
| | Reintento tras fallo transitorio | I | Sin fragmentos duplicados |
| | Caída simulada antes y durante la confirmación | I | Rollback completo |
| **Tokens** | Tokens informados | I | `AIUsageLog` con el consumo real sumado |
| | Tokens no informados | I | **No se escribe 0**: sin fila de `AIUsageLog`; auditoría con `tokensInformados = null` |
| | Estimación preventiva | I | `documento_indices.TokensTotales` y el presupuesto usan la estimación local; los campos de tokens de `AIUsageLog` contienen solo lo informado por el proveedor, aunque la estimación difiera |
| | Actor | I | `Origen = Worker`, `UsuarioId = NULL`, `ActorSistema` informado; `CostoEstimadoUsd = NULL` |
| | Ningún lote completado | I | Sin fila de `AIUsageLog` |
| **Hash** | Hash distinto tras la extracción y en la confirmación | I | `Fallido` con `DOCUMENT_TEXT_INVALID`; evento de seguridad |
| | Ciclos posteriores | I | **No se reencola** ni se vuelve a sembrar |
| **Batch** | 64 fragmentos | I | Un lote de 64 |
| | 65 fragmentos | I | Dos lotes (64 + 1); un lote de 65 es rechazado por la validación de la 8.3 |
| | Proveedor configurado por encima de 64 | I | Lotes de como máximo 64 |
| | 1, 130 y 2.000 fragmentos | I | Orden conservado; lotes secuenciales |
| **Documento** | Nuevo: TXT, PDF, DOCX, XLSX | I | `Indexado`; fragmentos y vectores correctos |
| | PUT de metadatos | I | No reindexa |
| | Borrado en `Pendiente`, `Procesando` e `Indexado`; borrado concurrente con la confirmación | I | `PurgaPendiente`; nunca activo; purga |
| | Más de 2.000 fragmentos | I | `DOCUMENT_INDEX_TOO_LARGE`; 0 llamadas al proveedor |
| | La indexación no toca `EstadoIa` ni `MetadatosJson` | I | Sin cambios |
| **Multi-tenant** | El worker de A no lee documentos de B | I | Verificado |
| | Fragmento o índice que apunta a otro tenant | I | La FK lo rechaza |
| | Contenido idéntico en dos tenants | I | Índices independientes |
| | Tenant desactivado con trabajos en curso | I | Abandono; purga con `TENANT_DESACTIVADO` |
| **Presupuesto** | Presupuesto preventivo agotado | I | Aplazado al día siguiente; ni `Fallido` ni intento |
| **Seguridad** | Logger de captura | I | Sin texto, vectores, rutas ni mensajes de excepción |
| | Transporte capturado | I | Solo textos de fragmentos de un documento por lote |
| **Arquitectura** | Sin endpoints, sin búsqueda, sin migraciones | U | Verificado |
| **Rendimiento** | Benchmarks de §26.4 | I | Mediciones documentadas con su entorno |
| **E2E** | Documento subido → ciclo del worker → índice `Indexado` | E2E | Verificado |
| **Regresión** | Suites de las Fases 0–8.3 | I y E2E | Verdes |

## 29. Acceptance Criteria

### 29.1 Preguntas que el contrato responde

| # | Pregunta | Respuesta |
|---|---|---|
| 1 | ¿Cómo se crea un trabajo? | El sembrado inserta una fila `Pendiente` en `documento_indices` (§9) |
| 2 | ¿Quién lo reclama? | Cualquier instancia del worker, en la adquisición (§10) |
| 3 | ¿Cómo se evita el doble proceso? | `FOR UPDATE SKIP LOCKED`, estado `Procesando`, lease y xmin (§8, §10) |
| 4 | ¿Qué ocurre si muere un worker? | El lease vence; nada quedó escrito (§11) |
| 5 | ¿Cómo se recupera un lease? | Paso de recuperación: `Pendiente` con `Intentos + 1` (§11) |
| 6 | ¿Cómo se reintenta? | Backoff exponencial con jitter, hasta 5 intentos (§12) |
| 7 | ¿Qué errores son transitorios? | Los `EsTransitorio` del proveedor, `ExtractionFailed` y los transitorios de la base (§13) |
| 8 | ¿Cuáles no se reintentan? | Los permanentes del proveedor, de extracción y de fragmentación, y todo error interno (§13) |
| 9 | ¿Cómo se garantiza la idempotencia? | Únicos parciales, xmin, lease y fragmentos solo en la confirmación (§8) |
| 10 | ¿Cómo se versiona? | Una fila por versión; una vigente y una en curso (§7) |
| 11 | ¿Cómo se evita publicar un índice incompleto? | Fragmentos y activación en una sola transacción (§18, §19) |
| 12 | ¿Cómo se activa atómicamente? | Secuencia de §19; invariante de §20 |
| 13 | ¿Y si falla después de persistir embeddings? | No existe ese estado: se confirma todo o nada |
| 14 | ¿Y si el documento cambia? | El contenido es inmutable; el hash lo protege: `DOCUMENT_TEXT_INVALID` (§14) |
| 15 | ¿Y si se elimina? | `PurgaPendiente`; nunca se activa (§24) |
| 16 | ¿Cómo se mantiene el aislamiento? | FK compuestas, scopes, identificadores copiados del índice (§21) |
| 17 | ¿Cómo se limita el lote a 64? | `min(proveedor.MaxEntradasPorLote, 64)` más la validación de la 8.3 (§17) |
| 18 | ¿Cómo se evita saturar al proveedor? | Lotes secuenciales, `MaxParalelismo`, tope por tenant, enfriamiento y presupuesto preventivo (§12, §26) |
| 19 | ¿Cómo se registra el consumo? | Un `AIUsageLog` por intento con consumo real íntegro; limitación documentada si no se informa (§22) |
| 20 | ¿Qué recibe la 8.5? | Las garantías de §29.2 |

### 29.2 Contrato que la 8.4 entrega a la 8.5

Para toda fila de `documento_indices` con `Estado = Indexado`:
1. **Completitud:** tiene exactamente `Fragmentos` filas en `documento_fragmentos`, con `Orden` contiguo desde 0.
2. **Compatibilidad:** todos sus embeddings tienen 1536 dimensiones y proceden del proveedor, modelo y versiones que declara su `Perfil`.
3. **Unicidad:** como mucho un índice vigente por documento y perfil.
4. **Aislamiento:** `TenantId`, `DocumentoId` y `ExpedienteId` de cada fragmento coinciden con los de su índice.
5. **Atomicidad:** solo tienen fragmentos los índices `Indexado` y los `Obsoleto` o `PurgaPendiente` a la espera de purga.
6. **Estabilidad:** `HashFragmento`, `Orden`, offsets y `Ubicacion` no cambian mientras el índice existe.

La 8.5 debe filtrar por su cuenta por `Estado = Indexado`, por el **perfil activo**, por `TenantId` y `ExpedienteId`, y unir con documento y expediente activos: la 8.4 no garantiza que un documento borrado ya esté purgado. La cobertura se calcula con los datos que deja la 8.4, incluidos los `Fallido` y su `CodigoError`.

### 29.3 Criterios PASS / FAIL de la implementación

| # | Criterio | Verificación |
|---|---|---|
| A1 | Matriz de §28 en verde | Pruebas |
| A2 | Seis estados, sin estados ni columnas nuevos; **0 migraciones** | `dotnet ef migrations has-pending-model-changes`; sin archivos en `Migrations/` |
| A3 | Esquema de `AIUsageLog` sin cambios | Diff de la entidad y su configuración |
| A4 | Ningún `CodigoError` fuera del catálogo de §13.2 | Prueba |
| A5 | Invariante de atomicidad de §20 | Pruebas de activación |
| A6 | Nunca más de 64 textos por llamada al proveedor | Pruebas de lote |
| A7 | Tokens no informados nunca registrados como 0; estimaciones nunca en `AIUsageLog` | Pruebas de tokens |
| A8 | Parada limpia sin sumar intento; caída con `Intentos + 1` | Pruebas de parada |
| A9 | Hash distinto → `DOCUMENT_TEXT_INVALID`, sin reencolar | Pruebas de hash |
| A10 | Sin endpoints, búsqueda, RAG ni cambios en la 8.2 y la 8.3 | Prueba de arquitectura y diff |
| A11 | Logs y auditoría sin contenido, vectores ni secretos | Logger de captura |
| A12 | `dotnet build --no-incremental` con 0 errores y 0 advertencias | Comando |
| A13 | Suite completa en verde dos veces, con PostgreSQL estable | `dotnet test` ×2 |
| A14 | Sin paquetes vulnerables | `dotnet list package --vulnerable --include-transitive` |
| A15 | Benchmarks de §26.4 medidos y documentados | Informe |

### 29.4 Archivos de la futura implementación (referencia; no se crean ahora)

| Archivo | Cambio |
|---|---|
| `Application/Features/Indexacion/IIndexacionSemanticaService.cs` | Nuevo |
| `Application/Common/Indexacion/PerfilIndexacion.cs` | Nuevo |
| `Application/Common/Indexacion/CodigosIndexacion.cs` | Nuevo: catálogo cerrado |
| `Infrastructure/Services/IndexacionSemanticaService.cs` | Nuevo |
| `Infrastructure/BackgroundServices/IndexacionSemanticaBackgroundService.cs` | Nuevo |
| `Infrastructure/BackgroundServices/IndexacionOptions.cs` | Nuevo, con validador |
| `Infrastructure/DependencyInjection.cs` | Registro del servicio, las opciones y el worker |
| `tests/…/TestHostDefaults.cs` | `AI__Indexing__Enabled=false` |
| `tests/…/Fase8/Fase84*.cs` | Pruebas de §28 y el doble programable del proveedor |

Sin cambios en dominio, API, migraciones, Docker, extractor, fragmentador ni proveedores.

**Opciones `AI:Indexing`** (validadas al arrancar): `Enabled`, `IntervalSeconds = 15`, `MaxParalelismo = 2`, `MaxDocumentosPorTenant = 2`, `LeaseSeconds = 300`, `MaxIntentos = 5`, `MaxFragmentosPorDocumento = 2000`, `MaxTokensDiariosPorTenant = 5000000`, `LoteSembrado = 500`, `LotePurga = 200`. Ninguna es secreta y ninguna configura el tamaño del lote de embeddings.

## 30. Decisiones cerradas — Fase 8.4 v1.1

No queda ninguna pregunta abierta.

| # | Decisión cerrada |
|---|---|
| **K-1** | La regla "no implementa `IFalloProveedorEmbeddings` → error interno no reintentable" se aplica a las excepciones de `EmbedAsync` y a lo no previsto del proceso. Los errores transitorios de PostgreSQL conservan su propia política (estrategia de EF) y los estados tipados de la extracción, su clasificación de la 8.2. La cancelación del host no es un error (§13, §23) |
| **K-2** | Se mantienen exactamente los seis estados de `documento_indices`. No se crea ninguno para versiones, leases ni recuperación (§6) |
| **K-3** | El contenido de un documento es inmutable; "versión" es una fila de índice y no hay "modificación del documento" que reindexar. El hash protege ante un cambio inesperado (§7, §14) |
| **K-4** | **0 migraciones** en la 8.4, como fija el contrato rector §21 (§5) |
| **K-5** | `AIUsageLog` no guarda la relación con el trabajo ni la cantidad de textos: van en la auditoría y los logs (§22.4) |
| **DP-1** | Sin migración de `AIUsageLog` ni columna `TokensInformados`. Si el proveedor no informa tokens, no se escribe 0 ni una estimación: no se escribe la fila, y queda documentado como limitación y deuda técnica (§22.3) |
| **DP-2** | Sin columna `Generacion` ni contador: una fila de `documento_indices` es una versión (§7) |
| **DP-3** | `AIUsageLog.TokensEntrada`, `TokensSalida` y `TotalTokens` contienen únicamente consumo informado por el proveedor; nunca tokens estimados. Las estimaciones locales solo sirven para el presupuesto preventivo y la planificación interna. `documento_indices.TokensTotales` pertenece a otra entidad y es una estimación local del tamaño del texto indexado, no consumo del proveedor. El día del presupuesto es el día UTC (§22.1, §26.2) |
| **DP-4** | `CostoEstimadoUsd = NULL`. Sin precios codificados ni cálculo monetario especulativo (§22.2) |
| **DP-5** | Catálogo cerrado de diez códigos (§13.2). `ContextExceeded` → `DOCUMENT_INDEX_TOO_LARGE`; `Forbidden` → `INDEX_INTERNAL_ERROR`. La implementación no puede inventar códigos |
| **DP-6** | Hash distinto → `Fallido` con `DOCUMENT_TEXT_INVALID`, evento de seguridad y auditoría, sin reencolar ni usar `Obsoleto` (§14) |
| **DP-7** | Sin FK ni columna nueva entre `AIUsageLog` y el trabajo. Correlación por `ejecucionId` y `aiUsageLogId` en la auditoría y los logs (§22.4) |
| **DP-8** | Parada limpia: `Procesando → Pendiente` sin sumar intento. Caída: el lease vence y la recuperación suma `Intentos + 1` (§11, §23) |
| **DP-9** | Sin índices parciales por tenant. Cualquier optimización queda condicionada a mediciones (§5, §26.4) |
| **DP-10** | Sin endpoints de reindexación manual, activación ni administración: son de la 8.5. La 8.4 solo procesa los registros pendientes (§3) |

Las cuatro desviaciones respecto del contrato rector que se derivan de estas decisiones están en §31.

## 31. Desviaciones aprobadas respecto del contrato rector — Fase 8.4

Estas desviaciones son **INTENCIONALES y APROBADAS para la Fase 8.4**. No son errores del diseño ni cambios silenciosos: en la 8.4 rige lo que dice esta sección. Deberán **reconciliarse formalmente con `FASE_8_CONTRATO.md` en la actualización correspondiente de la Fase 8.7**; hasta entonces el contrato rector no se modifica.

### 31.1 Shutdown limpio

| | Regla |
|---|---|
| **Contrato rector** (§15) | Un trabajo cancelado no confirma nada y vuelve a `Pendiente` **mediante el lease** |
| **Fase 8.4 aprobada** | Durante una parada limpia: `Procesando → Pendiente` **de inmediato**; `Intentos` **no aumenta**; **no es un fallo** |
| **Sin cambio** | Una caída abrupta sigue usando el lease y la recuperación, y aumenta `Intentos + 1` |

Detalle en §11 y §23.

### 31.2 Hash mismatch

| | Regla |
|---|---|
| **Contrato rector** (§7) | Contemplaba descartar el trabajo y dejar el índice `Obsoleto` |
| **Fase 8.4 aprobada** | `Fallido`; `CodigoError = DOCUMENT_TEXT_INVALID`; evento de seguridad y auditoría; **sin reencolado automático**; **sin bucle** de reindexación |

Detalle en §14.

### 31.3 Tokens no informados

| | Regla |
|---|---|
| **Contrato rector** (§14) | Un registro de `AIUsageLog` por documento indexado, con tokens y coste reales |
| **Fase 8.4 aprobada** | **0 migraciones**; **no** se falsifica un `0` como consumo real; **no** se introduce consumo estimado en `AIUsageLog`; cuando el proveedor no informa el consumo no se escribe la fila, y queda como **limitación documentada del esquema actual**; el intento sigue siendo trazable por las demás fuentes (§22.4); `CostoEstimadoUsd = NULL` |

Detalle en §22.

### 31.4 Activación

| | Regla |
|---|---|
| **Contrato rector** (§8, paso 2) | El marcado pasa a `Obsoleto` los índices de perfiles inactivos |
| **Fase 8.4 aprobada** | La versión anterior **solo se retira dentro de la transacción de activación exitosa de la versión nueva**. Nunca existe un estado confirmado `N = Obsoleto` + `N+1 = Fallido` |

Detalle en §7, §19 y §20.

**Gate:** la implementación de la 8.4 solo puede empezar tras la aprobación explícita de este contrato. La 8.5 no puede empezar hasta que la 8.4 esté implementada, probada, auditada, commiteada y publicada, con HEAD igual a `origin/master` y el working tree limpio.
