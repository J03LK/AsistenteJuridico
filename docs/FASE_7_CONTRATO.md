# Fase 7 — Contrato consolidado del módulo de Documentos

Este documento recoge el estado **implementado** del módulo de documentos al cierre de la Fase 7 (subfases 7.1 a 7.5). Consolida los contratos aprobados de cada subfase (D1–D8, X1–X4, D73-*, D74-*, D75-*) y describe solo lo que existe en el código. Cuando este documento y un contrato de subfase difieren, prevalece este documento, porque refleja el código final.

## 1. Objetivo

Convertir el módulo de documentos en un componente seguro y coherente:

- todo documento pertenece a un expediente y a un tenant;
- el acceso se autoriza por permiso, rol y expediente antes de tocar el almacenamiento;
- se valida el contenido subido;
- el almacenamiento físico está protegido;
- la edición y el borrado tienen concurrencia optimista;
- la auditoría es transaccional y tiene integridad en la base de datos;
- el cliente Angular está alineado con la API.

## 2. Alcance

| Subfase | Contenido |
|---|---|
| 7.1 | Acceso, permisos y modelo: `ExpedienteId` obligatorio, FK RESTRICT, nuevas columnas `NombreArchivoOriginal` y `Descripcion`, códigos `DOCUMENT_*`, regla documental del AsistenteLegal también en el listado |
| 7.2 | Subida segura: validación de extensión, MIME y contenido; límite de 25 MiB; temporal con SHA-256 incremental; nombre físico GUID; defensas contra path traversal, symlinks y puntos de reanálisis |
| 7.3 | Listado oficial paginado; descarga endurecida; edición (PUT) y borrado lógico (DELETE) con xmin; bloqueo durante el procesamiento con IA; alias obsoleto con cabeceras de obsolescencia |
| 7.4 | Auditoría UPLOAD, UPDATE y DELETE transaccional; DOWNLOAD independiente; CHECK de formato del hash y de tamaño no negativo; invariantes de los documentos nuevos |
| 7.5 | Servicio y modelos Angular alineados con la API definitiva; pruebas unitarias del servicio; este contrato |

## 3. Exclusiones

No forman parte de la Fase 7 y **no** están implementados:

- **Reconciliación física**: cruzar archivos huérfanos con filas sin archivo. Hay 9 huérfanos de 2026-09-30, anteriores a la Fase 7.
- **Purga física**: el borrado es lógico y el archivo se conserva.
- **Bloqueo de la subida a expedientes cerrados o archivados**: no existe ninguna regla aprobada. Hoy se permite subir a cualquier expediente activo, no eliminado, al que el usuario tenga acceso de escritura.
- **Retirada del alias** `GET /api/v1/documentos/expediente/{id}`: se mantiene, sin `Sunset`.
- **Antivirus y análisis de contenido activo**: ver §25.
- **Pantallas Angular de documentos**: no existen componentes, rutas ni pantallas que consuman el servicio, así que no hubo integración real con el frontend (§25).
- **Cambios de CORS**: no se exponen cabeceras (D75-7).
- Las remediaciones de la Fase 6 (D-2, X1) y las dependencias de la Fase 8 (H10): ver §26 y §27.

## 4. Arquitectura del módulo

| Capa | Componente | Responsabilidad |
|---|---|---|
| API | `DocumentosController` | Rutas, políticas PBAC, límites de petición, cabeceras de descarga y del alias |
| API | `LimiteSubidaDocumentoAttribute` | Convierte el exceso del formulario de subida en 413 `DOCUMENT_SIZE_EXCEEDED` (sin este filtro, MVC respondía 400 ProblemDetails) |
| API | `GlobalExceptionMiddleware` | Envelope `ApiResponse` y `errors: [código]` |
| Application | `DocumentoDtos`, `DocumentoValidators`, `DocumentoErrorCodes`, `Permissions` | Contratos, validación, códigos y permisos |
| Infrastructure | `DocumentoService` | Orquestación: autorización → validación → reglas → persistencia → auditoría |
| Infrastructure | `ExpedienteAccessService` | Tenant, SuperAdmin, rol y expediente; regla documental del AsistenteLegal |
| Infrastructure | `FileStorageService`, `DocumentoContentValidator`, `NombreArchivoSanitizer` | Almacenamiento físico, validación de contenido y saneamiento del nombre |
| Infrastructure | `DocumentoConfiguration` y las migraciones `Fase71…`, `Fase74…` | Esquema, FK, CHECK, índices y xmin |
| Frontend | `documento.service.ts`, `fase4.models.ts` | Cliente HTTP y tipos alineados con la API (7.5) |

## 5. Modelo `Documento`

| Columna | Tipo | Notas |
|---|---|---|
| `Id` | uuid | PK |
| `TenantId` | uuid | NOT NULL; FK a `tenants` RESTRICT |
| `ExpedienteId` | uuid | **NOT NULL** (7.1); FK compuesta `(TenantId, ExpedienteId)` → `expedientes(TenantId, Id)` **RESTRICT** |
| `Titulo` | varchar(250) | obligatorio |
| `TipoDocumento` | varchar(100) | obligatorio |
| `Descripcion` | varchar(1000) | opcional (7.1) |
| `NombreArchivoOriginal` | varchar(255) | opcional; null en el histórico (7.1); nombre saneado, nunca nombre físico |
| `RutaAlmacenamiento` | varchar(500) | obligatoria; **interna**: nunca se expone en DTOs ni en la auditoría nueva |
| `ContentType` | varchar(100) | ContentType canónico del servidor, no el que declara el cliente |
| `TamanioBytes` | bigint | CHECK `>= 0` (7.4) |
| `HashSha256` | varchar(64) | null en el histórico; CHECK de formato (7.4) |
| `EstadoIa` | int | `Pendiente=0`, `Procesando=1`, `Procesado=2`, `Fallido=3` |
| `MetadatosJson` | jsonb | lo usa la extracción con IA (Fase 6); no se expone |
| `Version` | xmin | `IsRowVersion`; token de concurrencia optimista |
| `IsDeleted`, `DeletedAt`, `DeletedBy` | — | borrado lógico |
| `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy` | — | auditables |

**Índices:**
- `(TenantId, ExpedienteId)` filtrado `IsDeleted = false`;
- `(TenantId, EstadoIa)`;
- `(TenantId, TipoDocumento)`.

**DTO público `DocumentoDto`:**
- **Campos:** `id`, `tenantId`, `expedienteId`, `expedienteNumero?`, `titulo`, `tipoDocumento`, `descripcion?`, `nombreArchivoOriginal?`, `contentType`, `tamanioBytes`, `hashSha256?`, `estadoIa` (número), `estadoIaDescripcion`, `createdAt`, `createdBy?`, `updatedAt?`, `version`.
- **Nunca expone** `RutaAlmacenamiento`, `MetadatosJson`, `UpdatedBy` ni los campos de borrado.

**`ExpedienteDetailDto`:**
- Expone `DocumentosCount`, que llega al JSON como `documentosCount` por la política camelCase global (`Program.cs`). Es el número de documentos no eliminados.
- No expone una colección de documentos.
- En Angular el campo es `documentosCount: number` (7.5).

## 6. Multi-tenancy

- El tenant sale **siempre** del JWT (`ICurrentTenantService`), nunca del cliente.
- Hay un filtro global de EF por tenant y, además, condición explícita `TenantId == tenant` en el listado.
- Un documento o expediente de otro tenant da 403 `DOCUMENT_ACCESS_DENIED` en el acceso por id, o el 403 o 404 del expediente en el listado.
- SuperAdmin no tiene acceso a documentos jurídicos (403).
- La FK compuesta impide asociar un documento a un expediente de otro tenant.

## 7. Autorización (PBAC)

**Permisos:** `Documentos.Read`, `Documentos.Upload`, `Documentos.Update` y `Documentos.Delete`, como políticas por endpoint.

| Rol | Read | Upload | Update | Delete | Restricción de expediente |
|---|---|---|---|---|---|
| AdminEstudio | ✔ | ✔ | ✔ | ✔ | todos los del tenant |
| AbogadoSenior | ✔ | ✔ | ✔ | ✔ | todos los del tenant |
| AbogadoJunior | ✔ | ✔ | ✔ | — | solo los expedientes en los que es responsable |
| AsistenteLegal | ✔ | — | — | — | sin escritura; solo lee si tiene una tarea **Pendiente** o **EnProgreso** asignada en el mismo tenant y expediente |
| SuperAdmin | — | — | — | — | sin acceso (403) |

- **7.1 (D2):** la migración `Fase71…` retiró `Documentos.Upload` del AsistenteLegal en `role_claims`. Los JWT se emiten desde `Permissions.GetPermissionsForRole`.
- **Orden de comprobación en el servicio:**
  1. tenant y SuperAdmin;
  2. existencia del documento (no eliminado; 404);
  3. tenant (403);
  4. expediente activo: si el expediente está eliminado, el documento da 404;
  5. rol y escritura;
  6. regla del AsistenteLegal;
  7. validación;
  8. reglas de negocio.
- En el listado, un 403 del servicio de acceso se traduce a `DOCUMENT_ACCESS_DENIED`. El 404 de un expediente inexistente o eliminado **no lleva código**, porque no se trata de un documento inexistente.

## 8. Endpoints definitivos

Base: `/api/v1/documentos`. Todos requieren autenticación.

| Método | Ruta | Política | Respuesta |
|---|---|---|---|
| POST | `/upload` (multipart) | `Documentos.Upload` | 201 `ApiResponse<DocumentoDto>` |
| GET | `?expedienteId=&pageNumber=&pageSize=&tipoDocumento=&fechaDesde=&fechaHasta=&searchTerm=` | `Documentos.Read` | 200 `ApiResponse<PagedResult<DocumentoDto>>` |
| GET | `/{id}` | `Documentos.Read` | 200 `ApiResponse<DocumentoDto>` |
| GET | `/{id}/download` | `Documentos.Read` | 200, archivo (`attachment`) |
| PUT | `/{id}` | `Documentos.Update` | 200 `ApiResponse<DocumentoDto>` |
| DELETE | `/{id}?version={xmin}` | `Documentos.Delete` | 200 `ApiResponse<null>` |
| GET | `/expediente/{expedienteId}` | `Documentos.Read` | **OBSOLETO** (§21): 200 `ApiResponse<DocumentoDto[]>` |

## 9. Subida (upload)

**Formulario multipart:**
- `expedienteId`, `titulo` y `tipoDocumento` son obligatorios.
- `descripcion` es opcional.
- El archivo va en el campo **`file`**; no se acepta `archivo`.

**Validación de los campos:**

| Campo | Regla |
|---|---|
| `expedienteId` | no vacío |
| `titulo` | ≤ 250 caracteres |
| `tipoDocumento` | ≤ 100 caracteres |
| `descripcion` | ≤ 1000 caracteres |

**Límites:**
- Archivo de hasta **25 MiB**.
- Petición de hasta 26 MiB (`RequestSizeLimit` y `MultipartBodyLengthLimit`).
- Cualquier exceso da 413 `DOCUMENT_SIZE_EXCEEDED`: según el tamaño declarado, durante la copia o en el límite de Kestrel o del formulario.

**Flujo:**
1. Autorización de escritura sobre el expediente.
2. Saneamiento del nombre.
3. Extensión (415).
4. MIME declarado (415).
5. Tamaño declarado (413).
6. Copia a `{base}/.tmp/{guid}.upload` con SHA-256 incremental y corte en 25 MiB.
7. Validación de contenido (400).
8. Movimiento sin sobrescribir a `{tenant:N}/{expediente:N}/{guid:N}{ext}`.
9. El temporal se elimina siempre.

**Persistencia:**
- Las invariantes del documento nuevo se comprueban antes de guardar: hash SHA-256 válido y tamaño > 0.
- El INSERT y la auditoría UPLOAD van en el **mismo** `SaveChanges`.
- Si falla la base de datos o la auditoría, se borra el archivo físico (compensación con `CancellationToken.None`).
- `EstadoIa` inicial: `Pendiente`.

## 10. Validación de contenido

| Extensión | ContentType guardado | Validación |
|---|---|---|
| `.pdf` | `application/pdf` | firma `%PDF` |
| `.docx` | OOXML wordprocessingml | firma ZIP, `[Content_Types].xml` y `word/`; rechaza `vbaProject.bin` y entradas cifradas |
| `.xlsx` | OOXML spreadsheetml | firma ZIP, `[Content_Types].xml` y `xl/`; mismos rechazos |
| `.doc` / `.xls` | `application/msword` / `application/vnd.ms-excel` | firma OLE |
| `.txt` | `text/plain; charset=utf-8` | UTF-8 estricto (con o sin BOM), sin bytes NUL |
| `.jpg` / `.jpeg` | `image/jpeg` | firma JPEG |
| `.png` | `image/png` | firma PNG |

**Reglas generales:**
- **MIME declarado:** debe ser compatible con la extensión. Si está vacío o es `application/octet-stream`, se acepta. Si es contradictorio, 415 `DOCUMENT_TYPE_NOT_ALLOWED`.
- **Contenido:** un archivo vacío o con contenido que no corresponde al formato da 400 `DOCUMENT_MAGIC_BYTES_INVALID`.
- **Extensión fuera de la lista:** 415 `DOCUMENT_TYPE_NOT_ALLOWED`.
- **Nombre original** (`NombreArchivoSanitizer`):
  - sin ruta;
  - sin caracteres de control ni de formato (U+202E incluido);
  - normalizado a NFC;
  - sin espacios ni puntos finales;
  - ≤ 255 caracteres, conservando la extensión;
  - si queda vacío, 400.

## 11. Almacenamiento

- **Base:** `FileStorage:BasePath`. Sin esa configuración, `/app/storage` en Docker o `{AppContext.BaseDirectory}/storage`.
- **Ruta relativa nueva:** `{tenantId:N}/{expedienteId:N}/{guid:N}{ext}`. El nombre físico es siempre un GUID más la extensión.
- **Rutas del histórico:** `{tenant}/{archivo}` siguen siendo legibles.
- **Solo base de datos:** la ruta se lee siempre de la base, nunca del cliente.
- **Borrado lógico:** conserva el archivo y `RutaAlmacenamiento` (§15).

## 12. Defensas contra path traversal, symlinks y puntos de reanálisis

Se aplican en la escritura, la lectura y la compensación (`ResolveAndValidatePath`):

- **Rutas rechazadas:**
  - rutas absolutas o que empiezan por `/` o `\`;
  - componentes vacíos, que empiezan por `.` (incluidos `.` y `..`), o que contienen `\`, `:` u otro carácter inválido.
- **Contención en la base:** la ruta completa debe empezar por `base + separador`. Así, una carpeta vecina como `storage-evil` no cuenta como contenida. Las mayúsculas solo se ignoran en Windows.
- **Enlaces:** se rechaza cualquier symlink, junction o punto de reanálisis en los componentes existentes bajo la base, incluidos los symlinks rotos. Los directorios se crean comprobando antes y después.
- **Respuesta y log:** cualquier violación da 403, con log técnico `[STORAGE_PATH_TRAVERSAL]` o `[STORAGE_REPARSE_POINT]`. Esos logs incluyen la ruta relativa (D74-11), pero la respuesta HTTP no la incluye.

## 13. Descarga

- La autorización completa ocurre **antes** de abrir el archivo. Se abre solo la ruta guardada en la base, con las defensas de §12.
- **Cabeceras:**
  - `Content-Disposition: attachment` con `filename` y `filename*=UTF-8''…`;
  - `X-Content-Type-Options: nosniff`;
  - `Cache-Control: no-store`;
  - `Content-Type`: el canónico guardado.
- **Nombre del archivo:** `NombreArchivoOriginal`. En el histórico sin ese nombre, el `Titulo` saneado más la extensión física, o `documento{ext}` como último recurso. Nunca la ruta.
- **Errores:**
  - archivo inexistente: 404 `DOCUMENT_FILE_NOT_FOUND`, con log `[DOCUMENT_FILE_NOT_FOUND]` (id y tenant, sin ruta);
  - ruta insegura o enlace: 403.
- **Sin recálculo de SHA-256** en la descarga.
- **Angular (7.5, D75-7):**
  - `downloadDocumento(id)` pide `responseType: 'blob'` y devuelve solo el `Blob`;
  - **no** lee `Content-Disposition`, `Deprecation` ni `Link`, porque CORS no las expone y no se cambió;
  - el nombre para guardar sale del modelo con `nombreDescargaDocumento(doc)`: `nombreArchivoOriginal || titulo`.

## 14. PUT y xmin

**Cuerpo:** `{ titulo, tipoDocumento, descripcion, version }`.
- Cualquier otra propiedad del JSON se ignora.
- El servidor solo asigna `Titulo`, `TipoDocumento` y `Descripcion` (recortados; una descripción vacía pasa a null), más `UpdatedAt` y `UpdatedBy`.

**Orden de comprobación:**
1. autorización de escritura (404/403);
2. validación (400): versión ausente o `0` da 400;
3. `EstadoIa == Procesando` da 409 `DOCUMENT_PROCESSING`;
4. versión distinta de la cargada da 409 `DOCUMENT_CONCURRENCY_CONFLICT`;
5. `UPDATE … WHERE xmin = @version`: si afecta 0 filas, 409 `DOCUMENT_CONCURRENCY_CONFLICT`.

**Sin cambios:** si no cambia ningún campo, no se escribe y la versión no cambia.

**Respuesta:** el `DocumentoDto` con el **xmin nuevo**.

**Angular (7.5):** `updateDocumento(id, dto)` construye el cuerpo explícitamente con las 4 claves permitidas, aunque reciba un objeto con más propiedades.

## 15. DELETE y xmin

**Petición:** `DELETE /api/v1/documentos/{id}?version={xmin}`.

**Orden de comprobación:**
1. autorización de escritura (404/403);
2. versión ausente o `0`: 400;
3. `Procesando`: 409 `DOCUMENT_PROCESSING`;
4. versión obsoleta: 409 `DOCUMENT_CONCURRENCY_CONFLICT`;
5. `UPDATE` con `WHERE xmin`.

**Efecto:**
- Borrado **lógico**: `IsDeleted`, `DeletedAt` y `DeletedBy`.
- El archivo físico y `RutaAlmacenamiento` se conservan, porque no hay purga (§3).

**Angular (7.5):** `deleteDocumento(id, version)` envía la versión recibida como parámetro `version`.

## 16. Paginación

| Aspecto | Regla |
|---|---|
| Expediente | `expedienteId` obligatorio; vacío o ausente da 400 |
| `pageNumber` | `< 1` pasa a 1 |
| `pageSize` | 10 por defecto; `< 1` pasa a 10; `> 100` pasa a 100 (corrección, no error) |
| Filtros | `tipoDocumento` (exacto, ≤ 100); `fechaDesde`/`fechaHasta` sobre `CreatedAt` en UTC (desde ≤ hasta; si no, 400); `searchTerm` (≤ 200; sin distinguir mayúsculas, sobre `Titulo` y `NombreArchivoOriginal`) |
| Orden | total y determinista: `CreatedAt DESC, Id DESC` |
| Respuesta | `PagedResult`: `items`, `pageNumber`, `pageSize`, `totalCount`, `totalPages`, `hasPreviousPage`, `hasNextPage`; coincide con `PagedResponse<T>` de Angular |
| Angular | envía `expedienteId` siempre y los demás parámetros solo si tienen valor |

## 17. Auditoría

Entidad `Documento`, con `EntidadId` igual al id del documento.

| Acción | Mecanismo | Contenido |
|---|---|---|
| UPLOAD | `LogInTransactionAsync`, en el mismo `SaveChanges` que el INSERT | nuevos: `expedienteId`, `titulo`, `tipoDocumento`, `descripcion`, `nombreArchivoOriginal`, `contentType`, `tamanioBytes`, `sha256Contenido` |
| UPDATE | `LogInTransactionAsync`, en el mismo `SaveChanges` que el `UPDATE … WHERE xmin` | **solo los campos que cambian** (`titulo`, `tipoDocumento`, `descripcion`), anteriores y nuevos; sin xmin (D74-8) |
| DELETE | `LogInTransactionAsync`, en el mismo `SaveChanges` que el borrado lógico | anteriores: `expedienteId`, `titulo`, `nombreArchivoOriginal`, `sha256Contenido` (solo si el hash es válido) |
| DOWNLOAD | `LogAsync`, independiente, después de abrir el archivo | nuevos: `expedienteId`, `tamanioBytes` |

**Atomicidad:**
- Si falla la operación o hay un conflicto 409, no queda auditoría.
- Si falla la auditoría transaccional, no queda la operación (y en UPLOAD se borra el archivo).

**Fallo de DOWNLOAD:** si falla su auditoría, la descarga continúa y se registra el log `[AUDITORIA_NO_REGISTRADA]`.

**Clave `sha256Contenido` (D74-2):**
- Es exclusivamente el SHA-256 del contenido.
- Se usa en lugar de `hashSha256` porque el saneador de la auditoría oculta las claves que contienen "hash".

**Lo que no se audita:**
- La ruta física, en ningún evento nuevo.
- El contenido de los archivos.
- Los intentos denegados 403, 404 y 409 (D74-9); quedan en los logs técnicos.

**Histórico (D74-1):** los 68 eventos DELETE anteriores que contienen `rutaAlmacenamiento` se conservan sin cambios, porque la bitácora es inmutable.

## 18. CHECK de hash y tamaño

Migración `20261003223301_Fase74DocumentosIntegridad`:
- `CK_documentos_HashSha256_formato`: `"HashSha256" IS NULL OR "HashSha256" ~ '^[0-9a-f]{64}$'`;
- `CK_documentos_TamanioBytes_no_negativo`: `"TamanioBytes" >= 0`.

**Precondiciones:** la migración aborta sin cambios si hay filas incompatibles.

**Diseño compatible con el histórico:**
- El hash puede ser NULL y el tamaño 0 en filas antiguas.
- La obligatoriedad del hash y el tamaño > 0 de los documentos **nuevos** son invariantes de la aplicación (D74-5): `UploadDocumentoAsync` usa el regex `^[0-9a-f]{64}\z` y comprueba el tamaño > 0.
- No hay CHECK con fecha de corte ni `NOT VALID`.

## 19. Errores

**Formato de la respuesta:**
- Envelope `ApiResponse` con `success: false`, `message` y `errors`.
- 404, 403, 409, 413 y 415 llevan `errors: [código]`.
- Un 400 de contenido lleva `errors: [código, ...mensajes]`.
- Un 400 de validación de campos lleva solo los mensajes, sin código.

| Código | HTTP | Cuándo |
|---|---|---|
| `DOCUMENT_NOT_FOUND` | 404 | Documento inexistente, eliminado o con expediente eliminado |
| `DOCUMENT_ACCESS_DENIED` | 403 | Otro tenant, rol o expediente no permitido, o regla del AsistenteLegal |
| `DOCUMENT_FILE_NOT_FOUND` | 404 | Descarga autorizada pero sin archivo físico |
| `DOCUMENT_TYPE_NOT_ALLOWED` | 415 | Extensión no permitida o MIME contradictorio |
| `DOCUMENT_MAGIC_BYTES_INVALID` | 400 | Contenido vacío o que no corresponde al formato |
| `DOCUMENT_SIZE_EXCEEDED` | 413 | Más de 25 MiB, o petición de más de 26 MiB |
| `DOCUMENT_CONCURRENCY_CONFLICT` | 409 | Versión obsoleta o fila modificada entre la carga y el guardado |
| `DOCUMENT_PROCESSING` | 409 | PUT o DELETE con `EstadoIa = Procesando` |
| `DOCUMENT_HASH_MISMATCH` | — | **Reservado**; no se emite en la Fase 7 |

**Sin código:**
- Las defensas de almacenamiento devuelven 403 sin código.
- Un 404 de expediente en el listado no lleva código.

**Angular:**
- El tipo `DocumentoErrorCode` contiene exactamente estos 9 códigos.
- El servicio no transforma los errores, y los `errors` llegan intactos al consumidor.
- En la descarga (`responseType: 'blob'`) el cuerpo de error llega como `Blob` y el consumidor debe leerlo como texto para obtener `errors`.

## 20. Compatibilidad histórica

- Los documentos anteriores a la Fase 7 pueden tener null en `NombreArchivoOriginal`, `HashSha256` y `Descripcion`, y `TamanioBytes = 0`. Todos se siguen listando, leyendo, editando, borrando y descargando.
- Las rutas `{tenant}/{archivo}` antiguas siguen siendo legibles.
- Un DELETE de un documento sin hash omite `sha256Contenido` en la auditoría.
- Angular declara `nombreArchivoOriginal` y `hashSha256` como opcionales y nulables.
- **Documentos en expedientes eliminados (D75-5):** hay 78 activos en la base de desarrollo. Son inaccesibles (404) y no se cambiaron.

## 21. Alias obsoleto

`GET /api/v1/documentos/expediente/{expedienteId}`:
- Se **mantiene** (D75-4) y hace la misma consulta que la ruta oficial (autorización, tenant, orden y DTO), sin filtros ni paginación.
- **Cabeceras**, también en las respuestas de error:
  - `Deprecation: @1790985600` (RFC 9745; 2026-10-03T00:00:00Z);
  - `Link: </api/v1/documentos?expedienteId={id}>; rel="successor-version"`.
- No tiene `Sunset` ni fecha de retirada.
- **Consumidores Angular: 0** desde la 7.5. El servicio usa solo la ruta oficial.

## 22. Decisiones relevantes

**Fase 7.4:**

| # | Decisión |
|---|---|
| D74-1 | El histórico de auditoría no se toca (68 DELETE con ruta) |
| D74-2 | La huella va en la auditoría con la clave `sha256Contenido` |
| D74-3 | `HashSha256` se mantiene en `DocumentoDto` solo por compatibilidad con el contrato aprobado |
| D74-4 | CHECK de formato del hash (NULL o `^[0-9a-f]{64}$`) |
| D74-5 | El hash obligatorio de los documentos nuevos es una invariante de la aplicación, no una CHECK |
| D74-6 | CHECK de tamaño `>= 0`, compatible con el histórico |
| D74-7 | DOWNLOAD se audita con `LogAsync` después de abrir el archivo; su fallo no bloquea la descarga |
| D74-8 | UPDATE audita solo los campos que cambian, sin xmin |
| D74-9 | No se auditan los intentos denegados |
| D74-10 | Una migración con las dos CHECK |
| D74-11 | Se mantienen las rutas relativas en los logs técnicos de `FileStorageService` |
| D74-12 | Implementación en un único bloque con un único informe |

**Fase 7.5:**

| # | Decisión |
|---|---|
| D75-1 | Alinear el servicio y los modelos Angular con la API definitiva |
| D75-2 | Reconciliación física fuera de la Fase 7 |
| D75-3 | Sin purga física |
| D75-4 | Mantener el alias sin `Sunset` |
| D75-5 | Sin cambios para los documentos activos de expedientes eliminados: son inaccesibles (404) |
| D75-6 | D-2 y X1 son remediaciones obligatorias de la Fase 6, antes de la Fase 8 |
| D75-7 | Angular no lee cabeceras no expuestas: nombre desde `nombreArchivoOriginal` o `titulo`, sin cambios de CORS ni de `Program.cs` |
| D75-8 | En Angular, solo el tipo `DocumentoErrorCode`, sin interceptor global |
| D75-9 | La regla de expedientes cerrados o archivados queda fuera; no hay regla aprobada |
| D75-10 | La autorización del commit es aparte |

## 23. Pruebas realizadas

**Backend** (xUnit contra PostgreSQL real, `backend/tests/AsistenteJuridico.Domain.Tests/Fase7/`):

| Archivo | Cubre |
|---|---|
| `Fase71AccesoPermisosTests` | PBAC por rol, tenant, SuperAdmin, AsistenteLegal y Junior |
| `Fase71DocumentosApiTests` | Códigos `DOCUMENT_*`, DTO sin ruta y expediente eliminado |
| `Fase71EsquemaPostgreSqlTests` | NOT NULL, FK RESTRICT, columnas y `role_claims` |
| `Fase72ValidacionContenidoTests` | Firmas, OOXML, UTF-8 y MIME |
| `Fase72AlmacenamientoTests` | Temporal, GUID, traversal, symlinks y contención en la base |
| `Fase72SubidaApiTests` | Subida end-to-end, 413 (incluido Kestrel real), 415, 400 y compensación |
| `Fase73DocumentosApiTests` | Listado, filtros, paginación, alias y cabeceras, descarga, PUT, DELETE, versión 0/ausente/obsoleta, `Procesando` |
| `Fase73ConcurrenciaXminTests` | Conflicto detectado por PostgreSQL entre la carga y el guardado |
| `Fase74AuditoriaApiTests` | Contenido exacto de UPLOAD, UPDATE, DELETE y DOWNLOAD; ausencia de ruta |
| `Fase74AtomicidadAuditoriaTests` | Operación y auditoría todo o nada; fallo de DOWNLOAD que no bloquea |
| `Fase74IntegridadMigracionTests` | CHECK, precondiciones de la migración e invariantes |

**Frontend** (`fase4.services.spec.ts`, `HttpTestingController`), `DocumentoService`:
- listado, con y sin filtros, y nunca el alias;
- respuesta paginada, incluido el histórico con nulos;
- detalle;
- subida: `file` y no `archivo`, con y sin `descripcion`;
- PUT con un `DocumentoDto` completo como origen, que envía exactamente 4 claves;
- dos DELETE con pares distintos de id y versión;
- descarga de un `Blob` con `Content-Disposition` simulado que no se usa;
- `nombreDescargaDocumento`;
- conservación de `DOCUMENT_CONCURRENCY_CONFLICT` (409), `DOCUMENT_PROCESSING` (409) y `DOCUMENT_FILE_NOT_FOUND` (404, cuerpo `Blob`).

## 24. Resultados

Cifras exactas al cierre de la 7.5 (2026-10-03). Los casos de prueba se cuentan con `dotnet test --list-tests`.

| Suite | Resultado |
|---|---|
| Backend (`dotnet test`, ejecución 1) | 541/541 superadas, 0 con error, 0 omitidas |
| Backend (`dotnet test`, ejecución 2) | 541/541 superadas, 0 con error, 0 omitidas |
| De ellas, de la Fase 7 | 286 casos: 7.1 = 50 (28 + 14 + 8); 7.2 = 128 (29 + 14 + 85); 7.3 = 63 (58 + 5); 7.4 = 45 (21 + 12 + 12) |
| `dotnet build --no-incremental` | 0 errores, 0 advertencias |
| Angular (`ng test`) | 39/39 superadas (5 archivos); 13 de `DocumentoService` |
| `ng build` | correcto; 1 aviso de presupuesto de estilos de `dashboard.component.ts`, anterior a la Fase 7 y fuera de su alcance |

## 25. Limitaciones conocidas

- **No es un antivirus.** La validación comprueba la firma y la estructura. No detecta:
  - macros en DOC o XLS;
  - JavaScript en PDF;
  - un `.docm` renombrado sin `vbaProject.bin`.

  Tampoco distingue DOC de XLS, porque comparten la firma OLE.
- **El borrado lógico conserva los archivos:** sin purga, el almacenamiento crece.
- **Huérfanos y filas sin archivo:** hay 9 archivos huérfanos (anteriores a la Fase 7) y filas de pruebas sin archivo, sin reconciliar.
- **Expedientes cerrados o archivados:** se puede subir a ellos (sin regla aprobada).
- **Cancelación de la descarga:** cancelar una descarga puede dejar un log de error de auditoría (`LogAsync` cancelado). La descarga ya se había autorizado.
- **CORS:** no expone `Content-Disposition`, `Deprecation` ni `Link`, así que un cliente de navegador no puede leerlas (D75-7).
- **Sin integración real con el frontend:** no existen componentes, pantallas ni rutas Angular de documentos. La 7.5 solo alineó el servicio, los modelos y las pruebas unitarias.
- **Desalineaciones de Angular fuera de la Fase 7:** `ExpedienteDetailDto` sigue declarando colecciones (`tareas`, `audiencias`, `procesosJudiciales`) que el backend no devuelve en esa forma. La 7.5 solo corrigió `documentosCount`.
- **URL de la API:** está fija en el código (`http://localhost:5270`), igual que en el resto de servicios.
- **Base de datos compartida:** la base de desarrollo comparte datos con las pruebas de integración.

## 26. Dependencias de la Fase 6 (remediaciones pendientes)

Son obligatorias **antes de la Fase 8** (D75-6) y **no** se corrigieron en la Fase 7:

- **D-2:**
  - `AIService.SummarizeExpedienteAsync` no aplica la regla documental del AsistenteLegal (tarea vigente) al reunir los documentos del expediente.
  - **Pendiente** como remediación de la Fase 6.
- **X1:**
  - Documentos que se quedan en `EstadoIa = Procesando` (348 en la base de desarrollo) y que bloquean PUT y DELETE con 409 `DOCUMENT_PROCESSING`.
  - No hay mecanismo para recuperarlos.
  - **Pendiente** como remediación de la Fase 6.

## 27. Dependencias de la Fase 8

- **H10:**
  - `ReadDocumentTextSafelyAsync` (Fase 6) se traga los errores de lectura del archivo.
  - La Fase 8 (indexación/RAG) debe distinguir entre un archivo inexistente, una ruta insegura y un contenido ilegible antes de depender de esa lectura.
- **`DOCUMENT_HASH_MISMATCH`:** está reservado para la verificación o reconciliación futura con `HashSha256`.
- **Prerrequisitos:** D-2 y X1 (§26).
