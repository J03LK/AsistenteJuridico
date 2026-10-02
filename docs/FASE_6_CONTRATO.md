# Fase 6 — Contrato definitivo del Asistente Jurídico IA

Este documento recoge las decisiones contractuales tomadas al cierre de la Fase 6 (Fases 6.1, 6.2 y cierre). Complementa el diseño técnico aprobado v1.1.1 y prevalece sobre él en los puntos que aquí se indican.

## Rate limiting de IA (`AIRateLimit`)

| Cuota | Límite | Identidad |
|---|---|---|
| Por usuario | 15 solicitudes / minuto | claim `sub` del JWT |
| Por tenant | 60 solicitudes / minuto, sumando todos sus usuarios | claim `tenant_id` del JWT |

- Ambas cuotas coexisten: una solicitud se rechaza si agota cualquiera de las dos.
- Rechazo: HTTP 429, envelope `ApiResponse` con `errors: ["TOO_MANY_REQUESTS"]` y cabecera `Retry-After`.
- La IP nunca se usa como identidad para `AIRateLimit`. Las peticiones sin JWT no consumen cuota y la autorización las rechaza con 401.
- `AuthRateLimit` (Fase 3) es independiente y sigue basado en IP.
- Configuración: sección `RateLimiting:AI` (`UserPermitLimit`, `TenantPermitLimit`, `WindowSeconds`). Implementación en `Program.cs`: política de endpoint para la cuota por usuario y limitador global, que solo actúa en endpoints con `AIRateLimit`, para la cuota por tenant.
- Una solicitud rechazada por la cuota del usuario ya consumió un permiso de la cuota del tenant, porque la cuota del tenant se evalúa primero.

## Timeout del proveedor de IA

- `AI:OpenAICompatible:TimeoutSeconds = 60` en producción. Cubre la petición completa, incluidos los reintentos.
- En streaming, el timeout se aplica a la obtención de la respuesta y como tiempo máximo de inactividad entre fragmentos.
- Si el proveedor no responde a tiempo: **HTTP 502** con `errors: ["AI_PROVIDER_TIMEOUT"]`. **No se usa 504**: el timeout es un error controlado de integración con el proveedor y comparte el contrato 502 de `AIProviderException` (`AI_PROVIDER_ERROR`).
- Reintentos (v1.1.1 §18): máximo 2, con backoff exponencial y jitter, solo ante errores de red o respuestas 429/503 del proveedor.
- Cancelar desde el cliente detiene el procesamiento local, pero no garantiza que el proveedor deje de facturar lo ya procesado.

## Acceso documental del AsistenteLegal

Un AsistenteLegal accede a un Documento vinculado a un Expediente solo si existe una tarea que cumple **todas** estas condiciones:

1. Pertenece al mismo `TenantId`.
2. Pertenece al mismo `ExpedienteId` del documento.
3. Está asignada al usuario actual.
4. Su estado es `Pendiente` o `EnProgreso`.

Las tareas `Completada` y `Cancelada` no conceden acceso. En cualquier otro caso la respuesta es HTTP 403. La lista está en `ExpedienteAccessService.EstadosTareaQueHabilitanAccesoDocumental`, y la regla también aplica a los endpoints de documentos de la Fase 4 (`DocumentoService`).

## AIUsageLog

- `ConversationId` es una referencia histórica sin FK hacia `ai_conversations`. La migración `Fase62DropAIUsageLogConversationForeignKey` elimina esa FK.
- Los triggers `trg_ai_usage_logs_prevent_update` y `trg_ai_usage_logs_prevent_delete` bloquean cualquier UPDATE o DELETE (SQLSTATE 55000).
- La purga de conversaciones es estrictamente por tenant y nunca modifica ni elimina `ai_usage_logs`.

## Protocolo de errores del streaming (POST + SSE)

- Fallo antes del primer fragmento: error HTTP normal con envelope JSON (p. ej. 502).
- Fallo a mitad de la transmisión: bloque `event: error` con `data: {"code","message"}` y sin `data: [DONE]`.
- Cierre normal: `data: [DONE]`.
