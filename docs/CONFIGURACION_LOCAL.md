# Configuración local y secretos

Ningún secreto vive en el repositorio. Este documento lista solo los **nombres** de la configuración necesaria; los valores se generan y guardan en cada máquina.

## Backend fuera de Docker (desarrollo)

Se usa `dotnet user-secrets` del proyecto `backend/src/AsistenteJuridico.API`. Los tests de integración leen las mismas claves.

```bash
cd backend
dotnet user-secrets set "<clave>" "<valor>" --project src/AsistenteJuridico.API
```

| Clave | Uso |
|---|---|
| `Jwt:Key` | Clave de firma JWT: aleatoria, mínimo 32 bytes. Sin ella la API no arranca. |
| `ConnectionStrings:DefaultConnection` | Conexión a PostgreSQL (`Host=127.0.0.1;Port=5433;Database=asistente_juridico;Username=aj_user;Password=…`). Use `127.0.0.1`, no `localhost`: el contenedor solo escucha en IPv4 y `localhost` intenta primero IPv6 (`::1`), con unos 4 s de espera por conexión. |
| `DevSeed:Passwords:Admin` | Contraseña del usuario semilla AdminEstudio (tenant demo). |
| `DevSeed:Passwords:AbogadoSenior` | Contraseña del usuario semilla AbogadoSenior (tenant demo). |
| `DevSeed:Passwords:AbogadoJunior` | Contraseña del usuario semilla AbogadoJunior (tenant demo). |
| `DevSeed:Passwords:AbogadoTenantB` | Contraseña del usuario semilla del segundo tenant. |
| `DevSeed:Passwords:SuperAdmin` | Contraseña del usuario semilla SuperAdmin. |

- Las contraseñas semilla deben cumplir la política de Identity: al menos 8 caracteres, con mayúscula, minúscula, dígito y un carácter no alfanumérico.
- En Development, la API falla al iniciar indicando qué claves `DevSeed:Passwords:*` faltan.
- El seeder solo asigna contraseña al crear un usuario. Para cambiar la de un usuario que ya existe, use el flujo normal de cambio de contraseña.

Cualquier clave puede darse también como variable de entorno, sustituyendo `:` por `__` (por ejemplo `Jwt__Key`, `ConnectionStrings__DefaultConnection`, `DevSeed__Passwords__Admin`).

## Docker Compose (`.env`, no rastreado)

Se parte de `.env.example`.

| Variable | Uso |
|---|---|
| `POSTGRES_PASSWORD` | Contraseña del usuario de PostgreSQL. Obligatoria. |
| `JWT_SECRET` | Se pasa al backend como `Jwt__Key`. Obligatoria; la API rechaza el valor de ejemplo. |

PostgreSQL publica el puerto solo en `127.0.0.1`.
