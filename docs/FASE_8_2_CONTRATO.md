# Fase 8.2 — Contrato de extracción segmentada, normalización y chunking

| Campo | Valor |
|---|---|
| Versión | 1.1 |
| Estado | **Diseño — A1–A7 y A9 aprobadas; A8 corregida en v1.1, pendiente de aprobación. Implementación NO autorizada.** |
| Fecha | 2026-10-04 |
| Base | commit `518e2132cd16a45cb3019477b2131e39bdb672ba` (Fase 8.1 cerrada y publicada) |
| Contrato rector | `FASE_8_CONTRATO.md` v1.1. En caso de conflicto prevalece ese contrato |
| Documentos relacionados | `FASE_6X_CONTRATO.md` (extractor, `ExtractionStatus`, códigos `DOCUMENT_TEXT_*`), `FASE_7_CONTRATO.md` (catálogo `DOCUMENT_*` y Adenda A1) |

Este documento desarrolla la subfase **8.2** tal como la define `FASE_8_CONTRATO.md` §21:

> **8.2 Extracción segmentada y chunking.** Alcance: `ExtractSegmentsAsync` con `ExtractionProfile` en el extractor de la 6.X; normalizador; chunker. Exclusiones: embeddings y almacenamiento. Migraciones: 0. Pruebas: U de chunking y de normalización; I de extracción por formato; suite de la 6.X intacta. PASS: criterio 8; las pruebas de la 6.X en verde. Depende de: 8.1.

No se modifica ninguna regla del contrato rector. Donde ese contrato no fija un detalle necesario para implementar sin ambigüedad, este documento lo señala en §16 como **ambigüedad con propuesta** y pide aprobación expresa.

### Cambios v1.0 → v1.1

| Cambio | Secciones |
|---|---|
| A1–A7 y A9 aprobadas sin cambios | §16 |
| **A8 corregida:** se elimina la clave propuesta `AI:Extraction:IndexacionTimeoutSeconds`. El timeout del perfil `Indexacion` es la constante de 120 s ya fijada en `FASE_8_CONTRATO.md` §15. La 8.2 no añade configuración, y la configuración del worker o de la indexación (incluida su relación con el lease) queda para la 8.4 | §2, §5, §11, §14, §16, §18 (T43), §19 (C26), §21 |

---

## 1. Objetivo

Transformar un documento almacenado en una **lista ordenada y determinista de fragmentos de texto normalizado**, con ubicación, rango de caracteres, estimación de tokens y hash. Esa lista es la entrada que consumirá la 8.3 (embeddings) y la 8.4 (indexación).

```text
Documento (archivo en FileStorageService)
   └─▶ ExtractSegmentsAsync(perfil Indexacion)  → segmentos crudos con ubicación      [Infrastructure]
          └─▶ TextoNormalizador (norm-v1)        → segmentos normalizados              [Application, puro]
                 └─▶ Fragmentador (chunk-v1)     → fragmentos preparados o resultado   [Application, puro]
                                                    tipado (sin texto / límite superado)
```

La 8.2 **no escribe nada en la base de datos**, **no llama a ningún proveedor** y **no tiene llamadores en producción** salvo `ExtractAsync` de la 6.X, que pasa a compartir el recorrido de los archivos (§6.6).

## 2. Alcance y exclusiones

**Dentro de la 8.2:**
1. `ExtractionProfile` (perfiles `Chat` e `Indexacion`).
2. `IDocumentTextExtractor.ExtractSegmentsAsync` y sus tipos de resultado (`SegmentedExtractionResult`, `TextSegment`, `UbicacionSegmento`).
3. Ampliación de `DocumentTextExtractor` **sin duplicarlo**: un único recorrido de PdfPig y Open XML para ambos métodos.
4. `TextoNormalizador` con la versión `norm-v1`.
5. `Fragmentador` con la versión `chunk-v1`, incluida la comprobación del límite de fragmentos de §7.1 (detección y datos, sin cambio de estado).
6. Tipos de salida para la 8.3: `FragmentoPreparado` y `ResultadoFragmentacion`.
7. Pruebas unitarias y de integración de estos componentes, y la regresión completa de la 6.X.

La 8.2 **no añade claves de configuración** (§14).

**Fuera de la 8.2** (permanecen en las subfases aprobadas):

| Tema | Subfase |
|---|---|
| `IEmbeddingProvider`, proveedor compatible con OpenAI, proveedor simulado de embeddings, vectores, privacidad del proveedor | 8.3 |
| Worker de indexación, sembrado, `SKIP LOCKED`, lease, latido, recuperación, reintentos, backoff, purga | 8.4 |
| Escritura en `documento_indices` y `documento_fragmentos`; transiciones de `EstadoIndexacion`; `CodigoError`, `FragmentosCalculados`, `LimiteAplicado` persistidos | 8.4 |
| Perfil de indexación completo (`{proveedor}:{modelo}@{dimensiones}|…`) | 8.4 (con los datos del proveedor de la 8.3) |
| `AIUsageLog` del worker; auditoría `DOCUMENT_INDEX*` | 8.4 |
| Reindexación (manual o por perfil) | 8.4 y 8.5 |
| Búsqueda vectorial y léxica, RRF, endpoints, RAG, citas, PSH | 8.5, PSH y 8.6 |
| Anonimización o eliminación de nombres del contenido | Ninguna subfase de la Fase 8 (§13) |

## 3. Estado actual verificado (commit `518e2132`)

| Elemento | Estado | Evidencia |
|---|---|---|
| `IDocumentTextExtractor` con `IsSupported` y `ExtractAsync` | Existe | `Application/Common/Interfaces/AI/IDocumentTextExtractor.cs` |
| `ExtractionStatus`: `Success`, `Empty`, `FileNotFound`, `Forbidden`, `UnsupportedFormat`, `InvalidContent`, `ExtractionFailed`, `ContextExceeded` | Existe | Mismo archivo. La cancelación del llamador no es un estado: se propaga `OperationCanceledException` |
| `DocumentTextExtractor`: TXT (UTF-8 estricto, sin NUL), PDF (PdfPig, `ContentOrderTextExtractor`, páginas unidas con `\n\n`), DOCX (todos los párrafos, incluidos los de tablas, cada uno seguido de `\n`), XLSX (`[Hoja: nombre]`, filas con celdas separadas por `\t`) | Existe | `Infrastructure/Services/AI/DocumentTextExtractor.cs` |
| Límite de 30.000 caracteres con corte temprano en límite + 1 (`ContextExceeded` sin leer todo) | Existe | `ContextWindowValidator.MaxDocumentCharacters` |
| Timeout de 30 s (`AI:Extraction:TimeoutSeconds`) con `StreamCancelable` | Existe | `DocumentTextExtractionOptions` |
| Defensas OOXML: `MaxCharactersInPart = 64 MiB` | Existe | Mismo archivo |
| Logs: solo id del documento y tipo de excepción; nunca ruta ni contenido | Existe | Mismo archivo |
| Códigos `DOCUMENT_TEXT_EMPTY`, `DOCUMENT_TEXT_UNSUPPORTED`, `DOCUMENT_TEXT_INVALID`, `DOCUMENT_TEXT_EXTRACTION_FAILED` | Existe | `AIDocumentErrorCodes.cs` (Fase 6.X, DA-12) |
| `DOCUMENT_FILE_NOT_FOUND`; `DOCUMENT_EXCEEDS_CONTEXT_LIMIT`; `Forbidden` = 403 sin código (DA-6) | Existe | `DomainExceptions.cs`; `FASE_6X_CONTRATO.md` §H10 |
| Heurística de tokens `max(1, ceil(longitud / 4))` | Existe | `EstimateTokens` de los proveedores de chat |
| `DocumentoFragmento`: `Orden` (≥ 0, único por índice), `Texto`, `Ubicacion` (jsonb), `RutaSeccion` (varchar 500), `CaracterInicio`, `CaracterFin` (≥ inicio), `TokensEstimados` (≥ 0), `HashFragmento` (`^[0-9a-f]{64}$`), `Embedding` | Existe (8.1) | `DocumentoFragmento.cs`, `DocumentoFragmentoConfiguration.cs` |
| `DocumentoIndice`: `HashContenido` (nullable), `ProcesandoDesde`, `ProcesadoPor`, `IndexadoEn`, `FragmentosCalculados`, `LimiteAplicado` | Existe (8.1) | `DocumentoIndice.cs`. La 8.2 no lo lee ni lo escribe |
| `ExtractSegmentsAsync`, `ExtractionProfile`, normalizador, fragmentador | No existe | — |

## 4. Arquitectura y responsabilidades

| Componente | Capa | Responsabilidad | E/S | Estado |
|---|---|---|---|---|
| `IDocumentTextExtractor` (ampliada) | Application | Contrato de extracción: `IsSupported`, `ExtractAsync` (sin cambios), `ExtractSegmentsAsync` (nuevo) | — | — |
| `ExtractionProfile`, `SegmentedExtractionResult`, `TextSegment`, `UbicacionSegmento` | Application | Tipos del contrato de extracción | — | Inmutables (records) |
| `DocumentTextExtractor` (ampliado) | Infrastructure | **Extrae**: abre el archivo solo mediante `IFileStorageService`, lo recorre con PdfPig u Open XML y produce segmentos crudos con ubicación. Un único recorrido por formato que alimenta a los dos métodos | Lectura del archivo | Sin estado |
| `TextoNormalizador` | Application | **Normaliza** una cadena con `norm-v1` (§7). Función pura | Ninguna | Sin estado |
| `Fragmentador` | Application | **Fragmenta** con `chunk-v1` (§8): normaliza cada segmento con `TextoNormalizador`, construye el texto del documento, calcula fragmentos, ubicaciones, rangos, tokens y hashes, y comprueba el límite de fragmentos. Función pura | Ninguna | Sin estado |
| `FragmentoPreparado`, `ResultadoFragmentacion` | Application | **Salida** para la 8.3 y la 8.4 (§9) | — | Inmutables |

**Reglas de capas:**
- El dominio no cambia.
- `TextoNormalizador` y `Fragmentador` no dependen de Infrastructure, de EF ni del sistema de archivos. Se pueden probar sin base de datos.
- El `Fragmentador` llama siempre al `TextoNormalizador`: no existe una ruta que fragmente texto sin normalizar.
- Ningún componente de la 8.2 recibe `TenantId`, `ExpedienteId`, títulos ni usuarios. El único identificador que circula es `DocumentoId`, usado exclusivamente para los logs y para el resultado de la extracción, igual que en la 6.X.

**Constantes de versión** (públicas, en Application), que la 8.4 usará para componer el perfil de indexación de `FASE_8_CONTRATO.md` §3.4:
- `ExtractionProfile.VersionIndexacion = "ext-v1"`;
- `TextoNormalizador.Version = "norm-v1"`;
- `Fragmentador.Version = "chunk-v1"`.

Cualquier cambio que altere la salida de un componente para una misma entrada exige subir su versión (`ext-v2`, `norm-v2`, `chunk-v2`). Así se respeta el versionado por perfil de §3.4.

## 5. `ExtractionProfile`

```csharp
public sealed record ExtractionProfile(string Nombre, int MaxCaracteres, int TimeoutSeconds)
{
    public const string VersionIndexacion = "ext-v1";
    // Chat:       MaxCaracteres = 30.000     (ContextWindowValidator.MaxDocumentCharacters); TimeoutSeconds = AI:Extraction:TimeoutSeconds (30)
    // Indexacion: MaxCaracteres = 3.000.000; TimeoutSeconds = 120   (ambos constantes de ext-v1, fijados en FASE_8_CONTRATO.md §15)
}
```

| Perfil | `MaxCaracteres` | `TimeoutSeconds` | Uso | Origen del valor |
|---|---|---|---|---|
| `Chat` | 30.000 | 30 | `ExtractAsync` (flujos de la 6.X) | Valores existentes de la 6.X, sin cambios |
| `Indexacion` | 3.000.000 | 120 | `ExtractSegmentsAsync` | `FASE_8_CONTRATO.md` §15 ("`Indexacion = 3.000.000 / 120 s`"), como constantes |

**Reglas:**
- **`Chat`** conserva exactamente el comportamiento actual de la 6.X: `MaxCaracteres` es `ContextWindowValidator.MaxDocumentCharacters`, y `TimeoutSeconds` sale de la opción existente `AI:Extraction:TimeoutSeconds` (30), con el mismo tratamiento que hoy (un valor ≤ 0 se sustituye por 30). La 8.2 no cambia esa opción ni su validación.
- **`Indexacion`**: `MaxCaracteres = 3.000.000` y `TimeoutSeconds = 120` son **constantes** de `ext-v1`, con los valores que ya fija el contrato rector (§15). **No se crea ninguna clave de configuración** para este perfil (corrección A8).
  - La opción existente `AI:Extraction:TimeoutSeconds` no se reutiliza para este perfil, porque ya tiene un significado aprobado: el timeout del perfil `Chat`.
  - Si el funcionamiento del worker exige más adelante que este timeout sea configurable o que se valide contra el lease, lo decidirá el contrato de la 8.4. La 8.2 no lo anticipa.
- Los perfiles son valores fijos (`Chat`, construido con la opción existente, e `Indexacion`). No se construyen perfiles arbitrarios en producción; las pruebas sí pueden hacerlo, por ejemplo con un timeout corto para la prueba de timeout.

## 6. `ExtractSegmentsAsync`

### 6.1 Firma y tipos

```csharp
Task<SegmentedExtractionResult> ExtractSegmentsAsync(
    Guid documentoId, string? rutaAlmacenamiento, string? contentType, ExtractionProfile perfil, CancellationToken ct);

public sealed record SegmentedExtractionResult(
    Guid DocumentoId, ExtractionStatus Status, IReadOnlyList<TextSegment> Segmentos, string? HashSha256Archivo);
    // Segmentos solo tiene elementos con Status = Success; en cualquier otro estado es una lista vacía.
    // HashSha256Archivo: ver §6.5 y la ambigüedad A7.

public sealed record TextSegment(string Texto, UbicacionSegmento Ubicacion, string? RutaSeccion);

public sealed record UbicacionSegmento(string Tipo, int Desde, int Hasta, string Etiqueta);
    // Tipo ∈ { "paginas", "parrafos", "hoja", "lineas" }   (FASE_8_CONTRATO.md §6.2)
```

- `ExtractionStatus` **se reutiliza tal cual**: no se añaden valores (`FASE_8_CONTRATO.md` §15).
- `Texto` de un segmento es el texto **crudo** extraído (sin normalizar). La normalización es responsabilidad del `TextoNormalizador`.
- Los segmentos se devuelven **en orden de documento**. El orden es parte del contrato.

### 6.2 Segmentos por formato

| Formato | Unidad de segmento | `Ubicacion` del segmento | `RutaSeccion` |
|---|---|---|---|
| PDF | Una página (texto de `ContentOrderTextExtractor`, como en la 6.X) | `paginas`, `Desde = Hasta = n` (número real de página, base 1), `Etiqueta = "página n"` | NULL |
| DOCX | Una unidad de bloque del cuerpo, en orden: cada párrafo fuera de tablas y **cada fila de tabla** (celdas unidas con ` \| `; el texto de una celda con varios párrafos se une con un espacio; las tablas anidadas se aplanan dentro de su celda) | `parrafos`, `Desde = Hasta = n` (ordinal de la unidad, base 1, contando también las vacías), `Etiqueta = "párrafo n"` | Ruta de sección vigente (§8.6) |
| XLSX | Una fila con al menos una celda con valor (celdas unidas con `\t`, el mismo separador que la 6.X); el valor de las fórmulas es el guardado en caché | `hoja`, `Desde = Hasta = número real de fila` (`RowIndex`; si falta, el ordinal de la fila en la hoja), `Etiqueta = "hoja {nombre}"` | NULL |
| TXT | El documento completo | `lineas`, `Desde = 1`, `Hasta = número de líneas`, `Etiqueta = "líneas 1–L"` | NULL |

- La granularidad de XLSX (una fila por segmento, con la hoja como límite) es la ambigüedad **A3**.
- Las páginas, unidades y filas sin texto **no generan segmento**, pero **sí consumen numeración**: la ubicación siempre corresponde al documento real.
- La ruta de sección de DOCX se calcula en el extractor porque depende de los estilos del archivo (§8.6).

### 6.3 Estados

`ExtractSegmentsAsync` aplica las mismas reglas que `ExtractAsync` de la 6.X, con el límite y el timeout del perfil recibido:

| Situación | Estado | Notas |
|---|---|---|
| `ContentType` no soportado (DOC, XLS, JPG, PNG, otros) | `UnsupportedFormat` | Sin abrir el archivo |
| Ruta vacía, inválida o archivo inexistente | `FileNotFound` | Log `[DOCUMENT_FILE_NOT_FOUND]` con el id del documento |
| Ruta insegura, symlink o punto de reanálisis | `Forbidden` | La operación se aborta. Log `[AI_DOCUMENT_STORAGE_FORBIDDEN]`, además del log de seguridad del almacenamiento |
| Archivo dañado, cifrado, UTF-8 inválido o con NUL (TXT), PDF sin páginas | `InvalidContent` | Log con el tipo de excepción |
| Timeout del perfil | `ExtractionFailed` | Log de advertencia con el timeout |
| Excepción inesperada del parser o de E/S | `ExtractionFailed` | Log con el tipo de excepción |
| La suma de caracteres crudos supera `perfil.MaxCaracteres` | `ContextExceeded` | Corte temprano en límite + 1, sin leer el documento completo |
| Ningún segmento con texto distinto de espacio en blanco | `Empty` | Mismo criterio que la 6.X (`string.IsNullOrWhiteSpace` sobre el texto crudo) |
| Cancelación solicitada por el llamador | — | Se propaga `OperationCanceledException`; nunca es un estado (§12) |
| Resto de casos | `Success` | Al menos un segmento |

**Cómputo de `MaxCaracteres`:** suma de las longitudes (en unidades UTF-16, igual que `string.Length`) de los textos crudos de los segmentos, **más** los separadores con los que `ExtractAsync` los concatena (§6.6). Así el perfil `Chat` aplicado por cualquiera de los dos métodos da exactamente el mismo resultado que hoy.

### 6.4 Defensas que se conservan

Todas las de la 6.X, sin excepción y sin código duplicado:
- formato decidido por el `ContentType` canónico guardado, nunca por el cliente;
- acceso al archivo **solo** mediante `IFileStorageService.OpenReadFileAsync` (traversal, symlinks y raíz validados);
- `StreamCancelable` y comprobación del token entre páginas, unidades y filas;
- espera al trabajo real, sin tareas abandonadas;
- `MaxCharactersInPart` en OOXML;
- TXT: UTF-8 estricto, sin NUL, lectura acotada a los bytes necesarios para detectar el exceso;
- nunca texto de respaldo ni inventado;
- logs sin ruta ni contenido.

### 6.5 Hash del archivo

`FASE_8_CONTRATO.md` §6.1 define `HashContenido` como el `HashSha256` del documento o, en los documentos históricos sin hash, "el calculado durante la extracción".

Propuesta (ambigüedad **A7**): con `Status = Success`, `ExtractSegmentsAsync` devuelve `HashSha256Archivo`, el SHA-256 en hexadecimal minúsculo de los **mismos bytes** que se analizaron, sea cual sea el estado de `Documento.HashSha256`.
- Se calcula en la misma lectura, así que no hay ventana entre el hash y el contenido extraído.
- Con cualquier otro estado es NULL.
- La 8.2 no decide qué hacer si difiere del `HashSha256` guardado: eso pertenece a la 8.4.

### 6.6 `ExtractAsync` (6.X) no cambia

`FASE_8_CONTRATO.md` §15: "`ExtractAsync` (6.X) **no cambia su contrato**: sigue con el perfil `Chat` y concatena los segmentos".

**Garantía exigida en la 8.2:** para todo archivo, `ExtractAsync` devuelve **exactamente** el mismo `ExtractionResult` que en el commit `518e2132`:
- mismo estado;
- texto idéntico carácter a carácter;
- mismos límites, timeout y logs.

**Implementación exigida:** un único recorrido interno por formato produce unidades estructuradas (página; párrafo o fila de tabla con sus celdas; fila de hoja con sus celdas y el nombre de la hoja). Dos *renderizados* las convierten:
- **Chat:** reproduce el formato actual de la 6.X: páginas con `\n\n`; cada párrafo, también los de tablas, seguido de `\n`; `[Hoja: nombre]` y filas con `\t`.
- **Segmentos:** §6.2.

Si se aplicara literalmente "concatena los segmentos", cambiaría el texto que la 6.X envía al LLM en los DOCX con tablas (` | ` en lugar de un párrafo por línea) y en los XLSX (sin la línea `[Hoja: …]`). Esa contradicción interna del contrato rector es la ambigüedad **A2**.

## 7. Normalización `norm-v1`

### 7.1 Definición

**Entrada:** una cadena de .NET (UTF-16), no nula: el texto crudo de un segmento.
**Salida:** una cadena de .NET. Puede quedar vacía o con solo espacios en blanco; el `Fragmentador` lo trata (§8.2).

**Transformaciones, en este orden exacto:**

| Paso | Transformación | Fuente |
|---|---|---|
| 1 | Normalización Unicode **NFC** (`string.Normalize(NormalizationForm.FormC)`) | §3.3 |
| 2 | Saltos de línea: `\r\n` → `\n`; después, `\r` aislado, U+0085 (NEL), U+2028 y U+2029 → `\n` | Interpretación de §3.3; ambigüedad **A4** |
| 3 | Se eliminan los caracteres de la categoría Unicode **Cc** (control), **salvo** `\n` (U+000A) y `\t` (U+0009) | §3.3 |
| 4 | Toda secuencia de **dos o más** caracteres de la categoría **Zs** (separadores de espacio, incluidos U+0020 y U+00A0) se sustituye por **un** U+0020. Un carácter Zs aislado no cambia | §3.3 ("los espacios repetidos se colapsan"); ambigüedad **A5** |

El orden importa: eliminar los controles antes de colapsar los espacios hace que `"a \u0001 b"` quede como `"a b"`, y no como `"a  b"`.

### 7.2 Lo que `norm-v1` NO hace (transformaciones prohibidas)

- No cambia mayúsculas ni minúsculas.
- No elimina tildes ni diacríticos. La búsqueda léxica usa `unaccent` en la columna generada, nunca en el texto.
- No cambia números, números de artículo, signos de puntuación, comillas ni guiones.
- No une las palabras partidas con guion al final de línea (eso queda para una `v2`, si se demuestra necesario).
- No colapsa ni elimina saltos de línea (`\n`) ni tabuladores (`\t`). Marcan la estructura de párrafos, líneas y celdas.
- No elimina caracteres de formato (categoría **Cf**: guion opcional U+00AD, espacio de ancho cero U+200B, BOM U+FEFF). `norm-v1` solo elimina la categoría **Cc**.
- No recorta los espacios al principio ni al final.
- **No anonimiza:** no elimina ni sustituye nombres de personas, clientes, abogados, partes, cédulas, RUC ni ningún otro dato del contenido. El contenido del documento es lo que se indexa (§13).
- No traduce, no corrige la ortografía y no resume.
- No depende de la cultura del proceso: no usa operaciones sensibles a la cultura.

### 7.3 Propiedades verificables

1. **Determinismo:** la misma entrada da siempre la misma salida, con cualquier cultura de proceso (por ejemplo, `es-EC`, `en-US`, `tr-TR`).
2. **Idempotencia:** `N(N(x)) = N(x)`.
3. **Preservación:** todo carácter de la salida aparece en la entrada (salvo los `\n` y U+0020 que producen los pasos 2 y 4), en el mismo orden relativo. La salida no es más larga que la entrada.
4. **Espacios en blanco:** si la entrada es vacía o solo contiene caracteres de espacio en blanco o de control, la salida es vacía o solo contiene espacio en blanco (`string.IsNullOrWhiteSpace(N(x))` es verdadero).

## 8. Fragmentación `chunk-v1`

### 8.1 Parámetros (de `FASE_8_CONTRATO.md` §3.3; constantes de `chunk-v1`)

| Parámetro | Símbolo | Valor |
|---|---|---|
| Tamaño objetivo | `OBJ` | 1.500 caracteres |
| Tamaño máximo | `MAX` | 2.000 caracteres. Límite duro de `FragmentoPreparado.Texto` completo, incluido el prefijo |
| Solapamiento | `SOL` | 200 caracteres, alineado al inicio de una frase si es posible |
| Tamaño mínimo | `MIN` | 200 caracteres |
| Prefijo máximo | `PREF` | 500 caracteres (la longitud de `RutaSeccion`, varchar(500), en el esquema de la 8.1); ambigüedad **A6** |

- Los caracteres se miden en unidades UTF-16 (`string.Length`), la misma unidad que `ContextWindowValidator` y `EstimateTokens`.
- **No son configurables:** cambiarlos cambia el resultado de la fragmentación, y eso exige `chunk-v2` (§3.4 y §7.1 del contrato rector).
- El límite de fragmentos por documento **sí** es configuración (§7.1); el `Fragmentador` lo recibe como argumento (§8.9).

### 8.2 Texto del documento y entrada efectiva

1. Cada segmento se normaliza con `norm-v1`.
2. Se **descartan** los segmentos cuyo texto normalizado es vacío o solo espacio en blanco. Su numeración ya quedó fijada en la ubicación.
3. Si no queda ningún segmento → resultado **`SinTexto`** (§8.9), que la 8.4 traducirá a `DOCUMENT_TEXT_EMPTY`. Cubre el texto vacío, solo espacios y solo caracteres de control.
4. **Texto del documento `T`:** concatenación de los segmentos normalizados, en orden, con este separador entre segmentos consecutivos:

| Formato | Separador | Motivo |
|---|---|---|
| PDF | `\n\n` | El mismo que la 6.X entre páginas |
| DOCX | `\n` | Cada unidad de bloque es una línea |
| XLSX | `\n` | Cada fila es una línea |
| TXT | (un solo segmento) | — |

5. `CaracterInicio` y `CaracterFin` (fin exclusivo) son posiciones en `T`.
6. **Entrada efectiva:** el espacio en blanco inicial y final de `T` no forma parte de ningún fragmento, porque no tiene contenido. Se fragmenta el intervalo `[primer carácter no blanco, último carácter no blanco]`. En XLSX, lo mismo por hoja.
7. **Unidades independientes:** en XLSX, cada hoja se fragmenta por separado, así que **ningún fragmento abarca dos hojas** y no hay solapamiento entre hojas. En PDF, DOCX y TXT, el documento entero es una sola unidad, así que un fragmento puede abarcar páginas o párrafos consecutivos (§3.3).

### 8.3 Puntos de corte

Un punto de corte es una posición de `T` **inmediatamente posterior** a un separador. Niveles, en el orden de prioridad del contrato rector:

| Nivel | Separador | Corte |
|---|---|---|
| 1 | Límite entre segmentos (el separador de §8.2) | Después del separador |
| 2 | Párrafo: `\n\n` dentro de un segmento | Después de `\n\n` |
| 3 | Salto de línea: `\n` | Después de `\n` |
| 4 | Frase: `. `, `; `, `: ` (signo seguido de U+0020) | Después del espacio |
| 5 | Corte duro | Ver §8.4, paso 4 |

### 8.4 Algoritmo

Para cada unidad (§8.2, punto 7), con `fin` igual al final de su entrada efectiva:

```text
inicioNuevo ← inicio de la entrada efectiva;   o ← inicioNuevo;   orden continúa la numeración global
repetir:
  prefijo ← prefijo del fragmento (§8.6, §8.7)
  B ← MAX − longitud(prefijo)                  // presupuesto del rango de T
  G ← min(OBJ, B)                              // objetivo del rango de T (incluye el solapamiento)
  1. si fin − o ≤ B:              e ← fin                                         (el resto cabe entero)
  2. si no, para nivel = 1..4:     C ← cortes de ese nivel en (o + MIN, o + G];    si C ≠ ∅: e ← máx(C); salir
  3. si no hubo, para nivel = 1..4: C ← cortes de ese nivel en (o + G, o + B];     si C ≠ ∅: e ← mín(C); salir
  4. si no hubo:                   e ← o + G, retrocediendo lo necesario para no partir un par sustituto
                                   ni dejar al inicio del siguiente un carácter combinante (Mn, Mc, Me)
  5. si T[inicioNuevo, e) es solo espacio en blanco: no se emite fragmento;
        inicioNuevo ← primer carácter no blanco desde e;  o ← inicioNuevo (sin solapamiento); continuar
  6. emitir fragmento con rango [o, e), prefijo, ubicación del rango y RutaSeccion vigente en inicioNuevo
  7. si e = fin: terminar
  8. inicioNuevo ← e;  o ← inicio del solapamiento (§8.5)
```

**Propiedades que se derivan del algoritmo** (y que las pruebas verifican):
- **Progreso:** como `MIN ≥ SOL`, todo corte de los pasos 2 y 3 cumple `e > o + MIN ≥ inicioNuevo`. El paso 4 corta en `o + G`, y `G ≥ 1.499 > MIN`. Cada fragmento aporta texto nuevo y el algoritmo termina.
- **Máximo:** el rango de `T` mide como mucho `B`, así que `longitud(prefijo) + longitud(rango) ≤ MAX`. **Ningún fragmento supera 2.000 caracteres.**
- **Fragmento final corto:** el contrato rector exige unir el último fragmento menor que `MIN` con el anterior si el resultado no supera el máximo. Esto se cumple por construcción:
  - si el resto hubiera cabido en el fragmento anterior, el paso 1 lo habría incluido;
  - si no cupo, la unión superaría el máximo y el fragmento corto se conserva, como permite el contrato.
- **Prioridad de niveles:** nunca se elige un corte de nivel inferior si, en la misma ventana, existe uno de nivel superior.

### 8.5 Solapamiento

Para el fragmento siguiente a uno que terminó en `e`:
1. Ventana: `[máx(e − SOL, o_anterior + 1), e)`.
2. `o` es el **primer** punto de corte de nivel 1 a 4 dentro de la ventana, es decir, el inicio de frase, línea, párrafo o segmento más antiguo.
3. Si no hay ninguno: `o = e − SOL`, ajustado hacia delante para no partir un par sustituto ni empezar con un carácter combinante.
4. El solapamiento es texto de `T` y forma parte del rango `[CaracterInicio, CaracterFin)` del fragmento.
5. **XLSX:** el primer fragmento de cada hoja no tiene solapamiento.

### 8.6 DOCX: ruta de sección y prefijo

- **Títulos:** un párrafo es título de nivel `n` (1 a 9) si su estilo (`StyleId` o nombre del estilo) coincide, sin distinguir mayúsculas, con `Heading n`, `Título n` o `Titulo n` (también sin espacio, como `Heading1` o el `StyleId` `Ttulo1` de Word en español). Es la regla "estilos Heading/Título" de §3.3.
- **Ruta vigente:** una pila por niveles. Un título de nivel `n` sustituye al de nivel `n` y elimina los de nivel mayor. La ruta se escribe con los textos normalizados de los títulos de la pila, de menor a mayor nivel, unidos con ` > `.
- **Vigencia:** un título está vigente desde el inicio de su propio párrafo. La `RutaSeccion` de un fragmento es la vigente en `inicioNuevo` (el primer carácter de texto nuevo, no el solapamiento). Sin títulos previos → NULL.
- **Límite de 500:** si la ruta supera 500 caracteres, se eliminan los niveles menos profundos y se antepone `… > ` hasta que quepa. Si el título más profundo por sí solo no cabe, la ruta es NULL (ambigüedad **A6**). El texto de los títulos sigue íntegro en el contenido del documento.
- **Prefijo:** si `RutaSeccion` no es NULL, `Texto = "Sección: " + RutaSeccion + "\n" + T[o, e)`. Es el "precede al fragmento como `Sección: …`" de §3.3, y el prefijo cuenta para el máximo.

### 8.7 XLSX: cabecera repetida

- La **cabecera** de una hoja es su primer segmento (primera fila con valor).
- El primer fragmento de la hoja ya la contiene como contenido.
- Los fragmentos 2.º y siguientes de esa hoja llevan el prefijo `cabecera + "\n"`.
- Si `longitud(cabecera) + 1 > 500`, la cabecera **no se repite**, para no agotar el presupuesto del fragmento (ambigüedad **A6**). Su contenido sigue en el primer fragmento.

### 8.8 Ubicación, tokens y hash de cada fragmento

| Campo | Regla |
|---|---|
| `Orden` | 0, 1, 2…, contiguo y global en el documento (en XLSX, hoja tras hoja en orden de libro). Cumple el `CHECK (Orden >= 0)` y el `UNIQUE (IndiceId, Orden)` de la 8.1 |
| `CaracterInicio`, `CaracterFin` | `o` y `e` en `T` (fin exclusivo). Excluyen el prefijo e incluyen el solapamiento |
| `Ubicacion` | Calculada sobre el rango `[o, e)`. `Tipo` = el de los segmentos. `Desde` y `Hasta` = el primer y el último segmento que toca el rango (PDF: páginas; DOCX: unidades; XLSX: filas reales) o, en TXT, la primera y la última línea de `T` que toca (base 1; un rango que termina justo después de `\n` no cuenta la línea siguiente) |
| `Etiqueta` | `"página n"` / `"páginas a–b"`; `"párrafo n"` / `"párrafos a–b"`; `"hoja {nombre}, fila n"` / `"hoja {nombre}, filas a–b"`; `"línea n"` / `"líneas a–b"`, con raya U+2013 entre `a` y `b` |
| `RutaSeccion` | §8.6 (solo DOCX; NULL en el resto) |
| `Texto` | `prefijo + T[o, e)` |
| `TokensEstimados` | `max(1, ceil(longitud(Texto) / 4))`, la convención de `EstimateTokens` (§3.3) |
| `HashFragmento` | SHA-256 de los bytes UTF-8 (sin BOM) de `Texto`, en hexadecimal **minúsculo** de 64 caracteres. Cumple el `CHECK` de formato de la 8.1 |

`Ubicacion` es un record en la 8.2. Su serialización a `jsonb` (`{ "tipo", "desde", "hasta", "etiqueta" }`, §6.2 del contrato rector) se hace al persistir, en la 8.4.

### 8.9 Límite de fragmentos (detección; el estado es de la 8.4)

De `FASE_8_CONTRATO.md` §7.1: la comprobación se hace "justo después del chunking, que se hace en memoria, y antes de llamar al proveedor de embeddings y de escribir nada en la base".

- El `Fragmentador` recibe `limiteFragmentos` (≥ 1) como argumento del llamador. El valor contractual es 2.000 (§7.1); su origen en configuración lo fija la 8.4.
- Siempre calcula **todos** los fragmentos, para conocer el número exacto.
- Si `n > limiteFragmentos`, devuelve **`LimiteSuperado`** con `FragmentosCalculados = n` y `LimiteAplicado = limiteFragmentos`, **sin ningún fragmento**: el resultado es todo o nada.
- La transición a `Fallido`, el código `DOCUMENT_INDEX_TOO_LARGE`, su persistencia y su auditoría son de la 8.4.

### 8.10 Casos límite (resultado exigido)

En DOCX el prefijo de sección reduce el presupuesto de cada fragmento. Los casos siguientes se entienden sin prefijo.

| Entrada efectiva | Resultado |
|---|---|
| Vacía, solo espacios o solo caracteres de control | `SinTexto` (→ `DOCUMENT_TEXT_EMPTY` en la 8.4). Si el texto crudo ya era espacio en blanco, el extractor devuelve antes `Empty` (§6.3) |
| Menos de 200 caracteres | **Un** fragmento (documento pequeño, §3.3) |
| Exactamente 200 caracteres | Un fragmento |
| Exactamente 1.500 caracteres | Un fragmento |
| Entre 1.501 y 2.000 caracteres | Un fragmento (paso 1: cabe entero en el máximo; ambigüedad **A1**) |
| Exactamente 2.000 caracteres | Un fragmento de 2.000 |
| 2.001 caracteres o más | Dos o más fragmentos, cada uno de 2.000 como máximo, con solapamiento de hasta 200 y cortes según §8.3 y §8.4 |
| Un segmento, párrafo o línea de más de 2.000 caracteres sin ningún separador | Cortes duros (paso 4) de `G` caracteres; nunca más de 2.000 |
| Texto con saltos de línea | Se conservan; son puntos de corte de nivel 3 (`\n`) o 2 (`\n\n`) |
| Tramo intermedio formado solo por espacios en blanco más largo que el objetivo | No genera fragmentos vacíos (paso 5) |

## 9. Salida para la 8.3 y la 8.4

```csharp
public sealed record FragmentoPreparado(
    int Orden, string Texto, UbicacionSegmento Ubicacion, string? RutaSeccion,
    int CaracterInicio, int CaracterFin, int TokensEstimados, string HashFragmento);

public enum EstadoFragmentacion { Fragmentado, SinTexto, LimiteSuperado }

public sealed record ResultadoFragmentacion(
    EstadoFragmentacion Estado, IReadOnlyList<FragmentoPreparado> Fragmentos,
    int? FragmentosCalculados, int? LimiteAplicado);
    // Fragmentado:    Fragmentos ≥ 1; FragmentosCalculados = Fragmentos.Count; LimiteAplicado = NULL
    // SinTexto:       Fragmentos vacío; ambos NULL
    // LimiteSuperado: Fragmentos vacío; FragmentosCalculados = n; LimiteAplicado = límite

// Fragmentador
ResultadoFragmentacion Fragmentar(IReadOnlyList<TextSegment> segmentos, int limiteFragmentos, CancellationToken ct);
```

**Correspondencia con `documento_fragmentos` (8.1)**, que hará la 8.4:
- `Orden`, `Texto`, `Ubicacion` (serializada), `RutaSeccion`, `CaracterInicio`, `CaracterFin`, `TokensEstimados` y `HashFragmento` se copian tal cual;
- `TenantId`, `DocumentoId`, `ExpedienteId` e `IndiceId` los pone la 8.4;
- `Embedding` lo produce la 8.3.

La 8.2 no añade campos al modelo de base de datos.

**Lo que la 8.3 enviará al proveedor** (definido en la 8.3, no aquí) será solo `FragmentoPreparado.Texto`, según §3.2.2 del contrato rector. `FragmentoPreparado` no contiene identificadores de tenant, documento ni expediente, títulos, rutas de almacenamiento ni usuarios. Los únicos metadatos que lleva (ubicación, hash, offsets) los descartará la 8.3, porque no son texto del fragmento.

**Validación de entrada del `Fragmentador`** (errores de programación, no códigos de negocio):
- `segmentos` nulo, o con segmentos de `Tipo` distinto entre sí;
- `Tipo` desconocido;
- `limiteFragmentos < 1`.

En todos esos casos lanza `ArgumentException`. Una lista vacía de segmentos da `SinTexto`.

## 10. Determinismo

**Garantía:** el mismo archivo (mismos bytes), con el mismo `ContentType`, el mismo `ext-v1` (perfil `Indexacion`), el mismo `norm-v1`, el mismo `chunk-v1` y el mismo `limiteFragmentos`, produce **el mismo `ResultadoFragmentacion`**: mismo estado, mismo número de fragmentos y, para cada `Orden`, los mismos `Texto`, `CaracterInicio`, `CaracterFin`, `Ubicacion`, `RutaSeccion`, `TokensEstimados` y `HashFragmento`.

**Criterios verificables:**
1. Dos ejecuciones sobre el mismo archivo dan listas idénticas, comparadas campo a campo.
2. Las pruebas **golden** de cada formato fijan la lista esperada de `HashFragmento`, offsets y ubicaciones. Cualquier cambio de salida rompe la prueba y obliga a subir la versión.
3. El resultado es idéntico con las culturas `es-EC`, `en-US` y `tr-TR`.
4. Ningún componente usa reloj, aleatoriedad, `Guid.NewGuid`, orden de diccionarios ni paralelismo que afecte al orden.
5. `Orden` es contiguo desde 0, y `CaracterInicio` es estrictamente creciente con `Orden` dentro de cada unidad.
6. **Identificación determinista** de un fragmento dentro de un perfil: `(Orden, CaracterInicio, CaracterFin, HashFragmento)`. La 8.2 no genera `Id` de fila; los `Id` los asigna la 8.4 al persistir.
7. **Reconstrucción:** sin el prefijo, `Texto` es exactamente `T[CaracterInicio, CaracterFin)`. La unión de los rangos cubre toda la entrada efectiva, salvo los tramos solo blancos del paso 5.

**Dependencias del determinismo** (riesgos R1 y R2): la versión de PdfPig y del Open XML SDK y los datos Unicode de la plataforma. Actualizar una biblioteca de extracción que cambie la salida exige subir a `ext-v2`.

## 11. Errores

La 8.2 **no crea códigos de error nuevos.** Reutiliza los existentes con su significado exacto y deja su persistencia a la 8.4.

| Origen | Resultado de la 8.2 | Código existente asociado | Dónde se define | Quién lo aplica |
|---|---|---|---|---|
| Texto vacío (extractor) | `ExtractionStatus.Empty` | `DOCUMENT_TEXT_EMPTY` | 6.X (DA-12) | 6.X (chat) y 8.4 (`CodigoError`) |
| Texto vacío tras normalizar (fragmentador) | `EstadoFragmentacion.SinTexto` | `DOCUMENT_TEXT_EMPTY` (mismo significado: no hay texto extraíble) | 6.X (DA-12) | 8.4 |
| Formato no soportado | `UnsupportedFormat` | `DOCUMENT_TEXT_UNSUPPORTED` | 6.X | 6.X y 8.4 |
| Contenido dañado, cifrado o ilegible | `InvalidContent` | `DOCUMENT_TEXT_INVALID` | 6.X | 6.X y 8.4 |
| Timeout o fallo inesperado de extracción | `ExtractionFailed` | `DOCUMENT_TEXT_EXTRACTION_FAILED` | 6.X | 6.X y 8.4 (transitorio con máximo de reintentos, §7 del contrato rector) |
| Archivo inexistente | `FileNotFound` | `DOCUMENT_FILE_NOT_FOUND` | Fase 7 | 6.X y 8.4 |
| Ruta insegura | `Forbidden` | 6.X: 403 sin código (DA-6); log `[AI_DOCUMENT_STORAGE_FORBIDDEN]` | 6.X | 6.X; 8.4 (observación O2) |
| Más caracteres que el perfil | `ContextExceeded` | Perfil `Chat`: `DOCUMENT_EXCEEDS_CONTEXT_LIMIT` (sin cambios). Perfil `Indexacion`: el contrato rector no fija código | Fase 6 | 6.X; 8.4 (observación O1) |
| Más fragmentos que el límite | `EstadoFragmentacion.LimiteSuperado` (con `n` y límite) | `DOCUMENT_INDEX_TOO_LARGE` | `FASE_8_CONTRATO.md` §7.1 y §15 | 8.4 |
| Cancelación del llamador | `OperationCanceledException` propagada | Ninguno: no es un error de negocio | 6.X | Llamador |
| Entrada inválida del `Fragmentador` (programación) | `ArgumentException` | Ninguno | Este contrato | — |

**Separación de categorías:**
- **Documento:** `FileNotFound`, `Forbidden`.
- **Extracción:** `UnsupportedFormat`, `InvalidContent`, `ExtractionFailed`, `Empty`.
- **Límites:** `ContextExceeded`, `LimiteSuperado`.
- **Normalización y fragmentación:** `SinTexto`.
- **Seguridad:** `Forbidden`, que además genera log de seguridad.
- **Configuración:** la 8.2 no añade opciones; la opción existente `AI:Extraction:TimeoutSeconds` conserva su tratamiento actual (§5, §14).

Ninguno de estos resultados llega a una respuesta HTTP en la 8.2, porque no hay endpoints nuevos.

## 12. Cancelación y timeout

- **Cancelación del llamador** (`ct`):
  - `ExtractSegmentsAsync` la propaga como `OperationCanceledException`, igual que `ExtractAsync`. Una excepción interna provocada por la cancelación se envuelve en `OperationCanceledException`;
  - nunca devuelve un resultado parcial ni un estado.
- **Timeout del perfil:** `ExtractionFailed`, con el mismo mecanismo de la 6.X (token enlazado, `StreamCancelable`, espera al trabajo real).
- **`Fragmentador.Fragmentar`:** comprueba `ct` antes de cada fragmento y antes de devolver. Si se cancela, lanza `OperationCanceledException` y no devuelve fragmentos.
- **`TextoNormalizador`:** síncrono y acotado por la longitud de un segmento; no recibe token. El `Fragmentador` comprueba el token entre segmentos.
- **No pertenece a la 8.2:** qué hace el worker con un trabajo cancelado (volver a `Pendiente` por lease), recuperación y reintentos son de la 8.4.

## 13. Multi-tenant y seguridad

- **No hay una vía nueva de lectura de documentos.** El único acceso al archivo es `IFileStorageService.OpenReadFileAsync`, con sus validaciones de la Fase 7.2 (traversal, symlinks, raíz).
- **Sin llamadores en producción:** la 8.2 no añade endpoints, servicios de aplicación ni workers que invoquen `ExtractSegmentsAsync`. Su único consumidor previsto es el servicio de indexación de la 8.4. Una prueba de arquitectura verifica que la API (controladores) no lo referencia.
- **Tenant y autorización:** el extractor no decide a qué tenant pertenece un documento, igual que en la 6.X.
  - El llamador debe obtener `RutaAlmacenamiento` y `ContentType` de un `Documento` cargado con el filtro global de tenant (`IMultiTenant`), y con el documento y el expediente activos.
  - En los flujos interactivos (6.X), además, tras `EnsureCanAccessDocumentosDeExpedienteAsync`.
  - Para la 8.4 rigen las reglas del contrato rector (§8: `SetTenantId` del tenant del documento; §4: FK compuestas).
  - La 8.2 no crea reglas de autorización nuevas.
- **Sin mezcla de tenants:** los componentes de la 8.2 trabajan sobre un solo documento por llamada, no tienen estado compartido ni cachés, y su salida no contiene identificadores de tenant.
- **Documento que deja de ser accesible:** la 8.2 no persiste nada, así que no hay nada que revertir. La revalidación del documento y el expediente activos antes de confirmar es de la 8.4 (§7 y §8 del contrato rector).
- **Contenido frente a metadatos:**
  - el texto de los fragmentos **es contenido del documento** y puede contener nombres de personas, clientes, abogados, partes y otros datos jurídicos. La 8.2 **no lo altera** para eliminarlos;
  - la prohibición de enviar al proveedor identificadores (`TenantId`, `DocumentoId`, `ExpedienteId`), títulos, rutas, hashes, credenciales, JWT, cookies y otros metadatos innecesarios se aplica en la 8.3 (§3.2.2 del contrato rector). `FragmentoPreparado.Texto` no contiene ninguno de esos metadatos.
- **Logs:** igual que en la 6.X y §13 del contrato rector:
  - **permitido:** id del documento, estado, tipo de excepción, conteos de segmentos y fragmentos, duraciones;
  - **prohibido:** texto de segmentos o fragmentos, rutas, nombres de hoja, rutas de sección, hashes de contenido.
- **Contenido original intacto:** la 8.2 solo lee. No modifica el archivo, ni el `Documento` (`EstadoIa`, `MetadatosJson`, `IaProcesandoDesde`, `HashSha256`), ni ninguna tabla.
- **Recursos:** el perfil `Indexacion` limita la extracción a 3.000.000 de caracteres, con corte temprano y 120 s, sobre archivos limitados a 25 MiB en la subida. Se mantienen `MaxCharactersInPart` y la lectura acotada de TXT.

## 14. Configuración

**La 8.2 no añade ninguna clave de configuración.**

| Clave | Valor por defecto | Nueva | Nota |
|---|---|---|---|
| `AI:Extraction:TimeoutSeconds` | 30 | No (6.X) | Perfil `Chat`. Sin cambios de nombre, valor ni tratamiento |

**No son configuración** (son constantes versionadas):
- `MaxCaracteres = 3.000.000` y `TimeoutSeconds = 120` del perfil `Indexacion` (`ext-v1`; valores de `FASE_8_CONTRATO.md` §15);
- `OBJ`, `MAX`, `SOL`, `MIN` y `PREF` (`chunk-v1`);
- las reglas de `norm-v1`.

**Configuración de la indexación** (sección `AI:Indexing`, entre ellas `AI:Indexing:MaxFragmentosPorDocumento`, y cualquier timeout propio del worker): pertenece a la 8.4. La 8.2 **no** la enlaza ni la define; el `Fragmentador` recibe el límite de fragmentos como argumento del llamador.

**Sin secretos:** la 8.2 no necesita ninguno y no añade claves sensibles a archivos rastreados.

## 15. Observaciones para la 8.3 y la 8.4 (no normativas en la 8.2)

Estos puntos los deberán resolver sus contratos; la 8.2 no los decide:

| # | Observación | Motivo |
|---|---|---|
| O1 | El código de `CodigoError` para `ContextExceeded` con el perfil `Indexacion` (más de 3.000.000 de caracteres) no está fijado en §7 del contrato rector. Candidatos: reutilizar `DOCUMENT_EXCEEDS_CONTEXT_LIMIT` o tratarlo como `DOCUMENT_INDEX_TOO_LARGE` (sin `FragmentosCalculados`) | Queda para la 8.4. No se inventa un código |
| O2 | §7 del contrato rector dice que `Forbidden` deja el índice `Fallido` con un "código de almacenamiento" no catalogado (`AI_DOCUMENT_STORAGE_FORBIDDEN` es hoy una etiqueta de log, y la 6.X responde 403 sin código) | Queda para la 8.4 |
| O3 | La comparación entre `HashSha256Archivo` (§6.5) y `Documento.HashSha256`, y su efecto | Queda para la 8.4 (§7: "cambio de hash durante la indexación") |
| O4 | La 8.3 debe descartar de `FragmentoPreparado` todo salvo `Texto` al llamar al proveedor | §3.2.2 del contrato rector |

## 16. Ambigüedades detectadas y propuestas

Ninguna de ellas cambia una regla del contrato rector; todas fijan un detalle que el contrato no especifica.

**Estado de la revisión:** A1–A7 y A9 **aprobadas**; A8 **corregida** en la v1.1 y pendiente de aprobación.

| # | Ambigüedad | Texto del contrato rector | Propuesta | ¿Requiere aprobación? |
|---|---|---|---|---|
| A1 | Documento de 1.501 a 2.000 caracteres: ¿uno o dos fragmentos? | "Tamaño objetivo 1.500; máximo 2.000"; "Documento pequeño: un solo fragmento" | **Un** fragmento si la entrada efectiva (más el prefijo) cabe en el máximo. Evita dividir con solapamiento un texto que cabe entero. El objetivo rige cuando hay que dividir | Sí |
| A2 | `ExtractAsync`: "no cambia su contrato" frente a "concatena los segmentos" | §15 | Texto de `ExtractAsync` **idéntico carácter a carácter** al actual, mediante un único recorrido con dos renderizados (§6.6). Sin extractor duplicado y sin cambiar lo que la 6.X envía al LLM | Sí |
| A3 | XLSX: "segmento = hoja" frente a ubicación por "hoja X, filas a–b" | §3.3 | Un `TextSegment` por fila con valor, número real de fila y el nombre de la hoja en `Etiqueta`. La hoja es una unidad independiente: ningún fragmento abarca dos hojas (§8.2) | Sí |
| A4 | Saltos de línea `\r\n`, `\r`, NEL, U+2028 y U+2029 | "Se eliminan los caracteres de control salvo `\n` y `\t`" | Convertirlos a `\n` antes de eliminar los controles. Si se elimina `\r` sin más, un `\r` aislado uniría dos líneas | Sí |
| A5 | Qué es "colapsar espacios repetidos" | "Los espacios repetidos se colapsan" | Secuencias de 2 o más caracteres Zs → un U+0020. No afecta a `\t` ni a `\n`; un Zs aislado (por ejemplo, NBSP) no cambia | Sí |
| A6 | Prefijos (sección DOCX y cabecera XLSX): ¿cuentan para el máximo? ¿Tamaño máximo? | §3.3 los define, pero no su tamaño | Cuentan para `MAX`. Límite de 500 caracteres (el de `RutaSeccion` en la 8.1): la ruta recorta sus niveles más altos con `… > `; una cabecera de más de 500 no se repite | Sí |
| A7 | Origen del hash de los documentos históricos sin `HashSha256` | §6.1: "el calculado durante la extracción" | `ExtractSegmentsAsync` devuelve `HashSha256Archivo` de los mismos bytes analizados, siempre que el estado sea `Success` | Sí |
| A8 | Timeout del perfil `Indexacion` (**corregida en v1.1**) | §15: "`Indexacion = 3.000.000 / 120 s`" | **Constante de `ext-v1` = 120 s**, sin clave de configuración nueva. `AI:Extraction:TimeoutSeconds` sigue siendo solo del perfil `Chat`. La configurabilidad del timeout para el worker y su relación con el lease se dejan al contrato de la 8.4 | Corregida según la revisión |
| A9 | ¿El objetivo de 1.500 incluye el solapamiento? | §3.3 | Sí: el objetivo se aplica al rango de `T` (solapamiento más texto nuevo), y el máximo, al `Texto` completo con prefijo | Sí |

## 17. Riesgos

| # | Riesgo | Mitigación |
|---|---|---|
| R1 | Una actualización de PdfPig u Open XML SDK cambia el texto extraído | Versiones fijadas en el `.csproj`; pruebas golden por formato. Un cambio de salida obliga a `ext-v2` |
| R2 | Diferencias de datos Unicode entre plataformas (NFC con ICU o NLS) | Prueba de determinismo en el entorno de CI y en el de desarrollo. Los caracteres afectados son los asignados en versiones recientes de Unicode: impacto mínimo |
| R3 | Romper la 6.X al compartir el recorrido del extractor | A2: salida de `ExtractAsync` idéntica carácter a carácter; la suite completa de la 6.X; pruebas de equivalencia de texto con los fixtures existentes |
| R4 | Títulos de DOCX con estilos personalizados no detectados | Degradación sin error: el fragmento queda sin `RutaSeccion`. Ampliar la detección exige `ext-v2` |
| R5 | Memoria con documentos grandes (3.000.000 de caracteres y unos 2.000 fragmentos) | Del orden de decenas de MB por documento. La 8.4 limita el paralelismo (§8 del contrato rector) |
| R6 | Los cortes duros parten palabras | Solo como último recurso (texto sin separadores en unos 1.500 caracteres). El contrato rector no prevé un nivel de palabra, y añadirlo sería una regla nueva |
| R7 | Las ambigüedades A1–A9 se implementan de forma distinta a lo que se aprueba | Aprobación expresa de este contrato antes de implementar; pruebas que fijan cada decisión |

## 18. Plan de pruebas

**Tipos:** **U** = unitaria (sin E/S); **I** = integración con `FileStorageService` real en un directorio temporal y archivos de prueba generados. La 8.2 no necesita PostgreSQL.

| # | Área | Caso | Tipo | Esperado |
|---|---|---|---|---|
| T1 | Normalización | NFC: `e` + U+0301 → `é`; texto ya en NFC sin cambios | U | Salida exacta |
| T2 | | Tildes, `ñ`, mayúsculas, números de artículo y puntuación se conservan | U | Sin cambios |
| T3 | | `\r\n`, `\r`, NEL, U+2028 y U+2029 → `\n` (A4) | U | Salida exacta |
| T4 | | Controles Cc eliminados salvo `\n` y `\t`; Cf (U+00AD, U+200B, U+FEFF) conservados | U | Salida exacta |
| T5 | | Espacios: `"a   b"` → `"a b"`; `"a  b"` → `"a b"`; NBSP aislado se conserva; `\t\t` y `\n\n\n` se conservan (A5) | U | Salida exacta |
| T6 | | Control entre espacios (`"a \u0001 b"`) → `"a b"` | U | Orden de pasos |
| T7 | | Idempotencia, determinismo con las culturas `es-EC`, `en-US` y `tr-TR`, y salida nunca más larga que la entrada | U | Propiedades |
| T8 | | Nombres de personas, cédulas y RUC en el texto | U | Intactos (sin anonimización) |
| T9 | Chunking | Entrada de 0 caracteres, solo espacios o solo controles | U | `SinTexto` |
| T10 | | Menos de 200, exactamente 200, exactamente 1.500 | U | Un fragmento cada uno |
| T11 | | 1.800 y exactamente 2.000 (A1) | U | Un fragmento |
| T12 | | 2.001 y documentos largos | U | Varios fragmentos; todos de 2.000 o menos; solapamiento de 200 o menos |
| T13 | | Prioridad de cortes: segmento > `\n\n` > `\n` > frase | U | Corte en el nivel más alto disponible de la ventana |
| T14 | | Sin cortes en la ventana objetivo, pero sí en `(o + G, o + B]` | U | Corte más cercano al objetivo |
| T15 | | Texto de 5.000 caracteres sin separadores | U | Cortes duros de `G`; sin partir pares sustitutos (emoji) ni separar caracteres combinantes |
| T16 | | Solapamiento alineado al primer inicio de frase de la ventana; sin frase, exactamente 200 | U | Offsets exactos |
| T17 | | Fragmento final corto: resto que cabe → unido; resto que no cabe → fragmento corto | U | Según §8.4 |
| T18 | | Tramo intermedio solo de saltos de línea más largo que el objetivo | U | Sin fragmentos vacíos |
| T19 | | DOCX: ruta de sección con niveles 1–3, cambio de sección, prefijo `Sección: `, ruta de más de 500 (A6) | U | Ruta, prefijo y recorte exactos; `Texto ≤ 2.000` |
| T20 | | XLSX: cabecera repetida desde el 2.º fragmento de la hoja; cabecera de más de 500 no repetida; ningún fragmento abarca dos hojas; sin solapamiento entre hojas | U | Exacto |
| T21 | | Ubicación y etiquetas: páginas, párrafos, filas reales (con filas vacías intermedias) y líneas | U | Exacto, con raya U+2013 |
| T22 | | `Orden` contiguo desde 0; `CaracterInicio` creciente; `Texto` sin prefijo = `T[inicio, fin)`; los rangos cubren la entrada efectiva | U | Propiedades |
| T23 | | `TokensEstimados = max(1, ceil(len/4))`; `HashFragmento` = SHA-256 UTF-8 en minúsculas (vector conocido) | U | Exacto |
| T24 | | Límite: `n = límite` → `Fragmentado`; `n = límite + 1` → `LimiteSuperado` con `n` y límite y 0 fragmentos | U | Exacto |
| T25 | | Determinismo: dos ejecuciones idénticas; golden de hashes por formato | U e I | Idénticas |
| T26 | | Cancelación antes y durante `Fragmentar` | U | `OperationCanceledException`; sin resultado |
| T27 | | Entradas inválidas (nulo, tipos mezclados, límite 0) | U | `ArgumentException` |
| T28 | Extracción | TXT, PDF de varias páginas (con una página vacía), DOCX con títulos y tabla, XLSX con dos hojas y filas vacías | I | `Success`; segmentos y ubicaciones exactos |
| T29 | | DOC, XLS, JPG, PNG | I | `UnsupportedFormat`, sin abrir el archivo |
| T30 | | Archivo vacío, PDF sin texto, XLSX sin valores | I | `Empty` |
| T31 | | PDF dañado, cifrado; DOCX corrupto; TXT con UTF-8 inválido o NUL | I | `InvalidContent` |
| T32 | | Archivo inexistente; ruta vacía; ruta insegura o symlink | I | `FileNotFound`; `Forbidden` (log de seguridad), sin texto |
| T33 | | Timeout del perfil `Indexacion` (con timeout de prueba) | I | `ExtractionFailed`; sin tarea abandonada |
| T34 | | Cancelación del llamador durante la extracción | I | `OperationCanceledException` |
| T35 | | Más de 3.000.000 de caracteres con el perfil `Indexacion`; más de 30.000 con el perfil `Chat` | I | `ContextExceeded` con corte temprano |
| T36 | | `HashSha256Archivo` = SHA-256 del archivo con `Success`; NULL en el resto (A7) | I | Exacto |
| T37 | | El archivo y el `Documento` no cambian tras extraer (hash del archivo y fila sin cambios) | I | Sin modificaciones |
| T38 | Regresión 6.X | `ExtractAsync` con los fixtures de la 6.X y de la 8.2: estado y texto idénticos a los valores de referencia capturados en `518e2132` (A2) | I | Idénticos |
| T39 | | Suite completa de la 6.X (extracción, resumen, extracción de hechos, timeout) | I y E2E | Verde |
| T40 | Seguridad | Ningún controlador ni servicio de la API referencia `ExtractSegmentsAsync` ni el `Fragmentador` | U (arquitectura) | Verificado |
| T41 | | Logger de captura durante la extracción y la fragmentación | I | Sin texto, rutas, nombres de hoja ni rutas de sección |
| T42 | | `FragmentoPreparado` no expone ids de tenant, documento ni expediente, títulos ni rutas | U (reflexión) | Verificado |
| T43 | Configuración | El perfil `Indexacion` vale 3.000.000 / 120 s; el perfil `Chat` sigue leyendo `AI:Extraction:TimeoutSeconds` con su tratamiento actual (≤ 0 → 30); no existen claves nuevas | U e I | Verificado |
| T44 | Regresión total | Suite backend completa | I y E2E | Verde dos veces |

**Aislamiento y autorización documental:** la 8.2 no añade vías de acceso, así que se cubren con T32 (almacenamiento seguro), T40 (sin exposición en la API), T42 (salida sin identificadores) y la suite de la 6.X (T39), que ya prueba la autorización por expediente en los flujos que extraen texto.

## 19. Criterios de aceptación (8.2 PASS / FAIL)

Cada criterio es binario. La 8.2 es PASS solo si se cumplen todos.

| # | Criterio | Verificación |
|---|---|---|
| C1 | Existe un único `DocumentTextExtractor`, con `ExtractSegmentsAsync` y `ExtractAsync` compartiendo el recorrido de PdfPig y Open XML (criterio 8 del contrato rector) | Revisión de código: sin un segundo extractor ni lógica de parseo duplicada |
| C2 | `ExtractAsync` devuelve estado y texto idénticos a los de `518e2132` para todos los fixtures | T38 |
| C3 | Suite de la 6.X en verde, sin modificar sus aserciones | T39; diff de las pruebas de la 6.X vacío |
| C4 | `ExtractionStatus` sin valores nuevos; la cancelación se propaga como `OperationCanceledException` | Revisión; T34 |
| C5 | Segmentos por formato con la ubicación de §6.2 | T28 |
| C6 | Estados de §6.3 para todos los casos de error | T29–T35 |
| C7 | `norm-v1` implementa exactamente §7.1 y nada de §7.2 | T1–T8 |
| C8 | `norm-v1` es determinista, idempotente e independiente de la cultura | T7 |
| C9 | El contenido no se anonimiza ni se altera fuera de §7.1 | T8; T22 (reconstrucción) |
| C10 | Ningún fragmento supera 2.000 caracteres, incluido el prefijo | T12, T15, T19, T20 y prueba de propiedad sobre todos los fixtures |
| C11 | Casos límite de §8.10 con el resultado exigido | T9–T11, T17, T18 |
| C12 | Prioridad de cortes, solapamiento y corte duro según §8.3–§8.5 | T13–T16 |
| C13 | Prefijos de sección y de cabecera según §8.6 y §8.7 | T19, T20 |
| C14 | `Orden`, offsets, ubicación, `TokensEstimados` y `HashFragmento` según §8.8, compatibles con los `CHECK` de la 8.1 | T21–T23 |
| C15 | Límite de fragmentos: todo o nada, con `FragmentosCalculados` y `LimiteAplicado` | T24 |
| C16 | Determinismo de §10, incluidas las pruebas golden por formato | T25 |
| C17 | Cancelación respetada en extracción y fragmentación, sin resultados parciales | T26, T33, T34 |
| C18 | Sin códigos de error nuevos; los existentes, con su significado | Revisión: búsqueda de constantes `DOCUMENT_*` y `AI_*` nuevas |
| C19 | Sin vías nuevas de lectura de documentos ni exposición en la API | T40; revisión de `IFileStorageService` como único acceso |
| C20 | Logs sin contenido, rutas, nombres de hoja ni rutas de sección | T41 |
| C21 | La 8.2 no escribe en la base de datos ni modifica el `Documento` ni el archivo | T37; revisión: sin `SaveChanges` ni `DbContext` en los componentes de la 8.2 |
| C22 | Sin migraciones y sin cambios en el modelo de EF | `dotnet ef migrations has-pending-model-changes` → sin cambios; sin archivos nuevos en `Migrations/` |
| C23 | Sin paquetes NuGet nuevos ni actualizados | Diff de los `.csproj` |
| C24 | Sin cambios en Docker ni en PostgreSQL | Diff de `docker/`, `docker-compose.yml` y `postgres/` vacío |
| C25 | Nada de la 8.3 o posteriores: sin `IEmbeddingProvider`, vectores, worker, `SKIP LOCKED`, lease, `AIUsageLog` nuevo, auditoría de indexación, búsqueda ni RAG | Búsqueda en el código de producción |
| C26 | Configuración: ninguna clave nueva; `AI:Extraction:TimeoutSeconds` sin cambios; perfil `Indexacion` con las constantes de §15; sin secretos | T43; diff de `appsettings*.json` y de `DocumentTextExtractionOptions` vacío |
| C27 | `dotnet build --no-incremental`: 0 errores y 0 advertencias | Comando |
| C28 | Suite backend completa en verde dos veces | `dotnet test` ×2 |
| C29 | `dotnet list package --vulnerable --include-transitive` sin hallazgos | Comando |
| C30 | `git diff --check` limpio; contrato actualizado con los resultados; commit y push autorizados | Revisión |

## 20. Dependencias y gates

**Depende de:**
- Fase 8.1 **CLOSED / PUBLISHED**, commit `518e2132cd16a45cb3019477b2131e39bdb672ba` (HEAD == origin/master, working tree limpio).
- Aprobación expresa de este contrato (A1–A7 y A9 ya aprobadas; falta la corrección de A8).

**La implementación de la 8.2 solo empieza** tras esa aprobación.

**La Fase 8.3 NO puede empezar** hasta que la 8.2 esté:
1. implementada;
2. probada (C1–C29 en verde);
3. auditada (revisión post-implementación aprobada);
4. commiteada;
5. enviada a `origin/master`;
6. con HEAD == origin/master;
7. con el working tree limpio.

**Relación con las demás subfases** (sin cambios respecto a `FASE_8_CONTRATO.md` §21):
- la 8.3 depende de la 8.1;
- la 8.4 depende de la 8.2 y de la 8.3;
- por la regla de gates anterior, la 8.3 empezará después del cierre de la 8.2.

## 21. Archivos esperados en la implementación (referencia; no se crean ahora)

| Archivo | Cambio |
|---|---|
| `Application/Common/Interfaces/AI/IDocumentTextExtractor.cs` | `ExtractSegmentsAsync`; tipos `ExtractionProfile`, `SegmentedExtractionResult`, `TextSegment`, `UbicacionSegmento` (en este archivo o junto a él) |
| `Infrastructure/Services/AI/DocumentTextExtractor.cs` | Recorrido compartido y dos renderizados; perfiles |
| `Application/Common/Indexacion/TextoNormalizador.cs` | Nuevo |
| `Application/Common/Indexacion/Fragmentador.cs` | Nuevo, con `FragmentoPreparado`, `ResultadoFragmentacion` y `EstadoFragmentacion` |
| `Infrastructure/DependencyInjection.cs` | Solo si hace falta registrar los componentes nuevos; sin opciones nuevas |
| `tests/.../Fase8/` | Pruebas de §18 y fixtures generados en las propias pruebas |

Migraciones: **0**. Paquetes: **0**. Docker y PostgreSQL: **sin cambios**.
