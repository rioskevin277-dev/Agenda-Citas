# AgendaApi — Agente de Citas Multi-Tenant por WhatsApp

Agente conversacional multi-tenant que agenda citas/espacios en calendario a través de WhatsApp, con integración a **Google Calendar** y **Microsoft 365 (Outlook Calendar)**.

---

## Arquitectura

```
┌──────────────────────────────────────────────────────────────┐
│                    AgendaApi.Api (Web API)                    │
│  Controllers · Middleware · Program.cs · Swagger             │
├──────────────────────────────────────────────────────────────┤
│              AgendaApi.Application (Use Cases)               │
│  CheckAvailability · CreateAppointment · CancelAppointment   │
│  RescheduleAppointment · SyncExternalChanges · SendReminders │
├──────────────────────────────────────────────────────────────┤
│              AgendaApi.Domain (Core + Ports)                  │
│  Entities: Tenant, CalendarConnection, ServiceType,          │
│  Appointment, Client, AvailabilityRule, AvailabilityException │
│  Ports: ICalendarProvider, IMessagingProvider, Repos, etc.   │
├──────────────────────────────────────────────────────────────┤
│          AgendaApi.Infrastructure (Adapters)                  │
│  GoogleCalendarAdapter · MicrosoftGraphCalendarAdapter        │
│  WhatsAppCloudApiAdapter · EF Core · Repositories            │
└──────────────────────────────────────────────────────────────┘
```

### ¿Por qué Hexagonal + Clean Architecture?

El punto de variación real de este proyecto es **qué proveedor de calendario usa cada tenant** (Google o Microsoft). Esa variación debe quedar aislada detrás de un puerto (`ICalendarProvider`), no filtrarse al dominio ni a los casos de uso. Hexagonal permite:

- **Intercambiar** Google Calendar por Microsoft 365 sin tocar una línea de lógica de negocio.
- **Añadir** un nuevo proveedor (Caldav, iCloud, etc.) con solo implementar `ICalendarProvider`.
- **Testear** cada adaptador de forma aislada mediante mocks de los puertos.

---

## Stack Tecnológico

| Capa | Tecnología |
|---|---|
| Lenguaje | C# 12 (.NET 8) |
| ORM | Entity Framework Core 8 |
| Base de datos | SQL Server |
| Mensajería | WhatsApp Cloud API (Meta) |
| Calendario | Google Calendar API v3 / Microsoft Graph API |
| Autenticación | JWT Bearer |
| Logging | Serilog |
| Documentación | Swagger / OpenAPI |

---

## Estructura del Proyecto

```
AgendaApi.sln
├── AgendaApi.Domain/           # Entidades + Puertos (interfaces)
│   ├── Entities/               # Tenant, Appointment, Client, etc.
│   ├── Enums/                  # AppointmentStatus, CalendarProviderType
│   └── Ports/                  # ICalendarProvider, IMessagingProvider, repositorios
│
├── AgendaApi.Application/      # Casos de uso
│   ├── DTOs/                   # AppointmentCreateDto, AvailabilityQueryDto
│   ├── UseCases/               # CheckAvailability, CreateAppointment, etc.
│   └── Services/               # ReminderBackgroundService
│
├── AgendaApi.Infrastructure/   # Adaptadores
│   ├── Data/                   # AgendaDbContext, GenericRepository, UnitOfWork
│   ├── Migrations/             # Migraciones EF Core
│   ├── Repositories/           # Implementaciones con EF Core
│   ├── CalendarProviders/      # GoogleCalendarAdapter, MicrosoftGraphCalendarAdapter
│   ├── Messaging/              # WhatsAppCloudApiAdapter
│   └── Middleware/             # TenantContext
│
├── AgendaApi.Api/              # Host
│   ├── Controllers/            # AppointmentController, WebhookController, TenantController
│   ├── Middleware/             # TenantEnricherMiddleware
│   └── Program.cs
│
└── README.md
```

---

## Entidades del Dominio

| Entidad | Descripción |
|---|---|
| **Tenant** | Negocio cliente (multi-tenant). Define qué proveedor de calendario usa. |
| **CalendarConnection** | Credenciales OAuth cifradas del tenant para su calendario. |
| **ServiceType** | Tipo de cita que ofrece: duración, buffer, precio opcional. |
| **AvailabilityRule** | Disponibilidad recurrente (ej: Lun-Vie 9-18hs). |
| **AvailabilityException** | Excepción puntual (feriados, horario especial). |
| **Appointment** | Cita agendada: cliente, fechas, estado, external event ID. |
| **Client** | Contacto de WhatsApp que agenda citas. |

---

## Configuración Rápida

### Prerrequisitos

- .NET SDK 8.0
- SQL Server (local o remoto)
- (Opcional) Cuenta de desarrollador en Meta para WhatsApp Cloud API
- (Opcional) Credenciales de Google Cloud / Azure AD para calendarios

### Levantar el proyecto localmente

```bash
# 1. Clonar el repositorio
git clone https://github.com/tu-org/agenda-api.git
cd agenda-api

# 2. Restaurar dependencias
dotnet restore

# 3. Configurar environment variables (ver tabla abajo)

# 4. Aplicar migraciones
dotnet ef database update --project AgendaApi.Infrastructure --startup-project AgendaApi.Api

# 5. Ejecutar
dotnet run --project AgendaApi.Api

# 6. Abrir Swagger
#    http://localhost:5000/swagger
```

### Variables de Entorno Requeridas

| Variable | Obligatoria | Descripción |
|---|---|---|
| `ConnectionStrings__AgendaDb` | ✅ Sí | Connection string de SQL Server |
| `Jwt__Secret` | ✅ Sí | Clave secreta JWT (mín. 32 chars) |
| `OpenAI__ApiKey` | ⚠️ Sí (sin AI falla) | API Key de OpenAI |
| `Anthropic__ApiKey` | ❌ No | Fallback si OpenAI falla |
| `TokenEncryption__MasterKey` | ✅ Sí | Clave AES-256 en Base64 (32 bytes) |
| `WhatsApp__VerifyToken` | ❌ No | Token de verificación webhook Meta |
| `GoogleOAuth__ClientId` | ❌ No | Google Cloud OAuth client ID |
| `GoogleOAuth__ClientSecret` | ❌ No | Google Cloud OAuth client secret |
| `MicrosoftOAuth__ClientId` | ❌ No | Azure AD app client ID |
| `MicrosoftOAuth__ClientSecret` | ❌ No | Azure AD app client secret |

> 💡 **Generar TokenEncryption__MasterKey:**
> ```bash
> # PowerShell
> [Convert]::ToBase64String((1..32 | ForEach { Get-Random -Min 0 -Max 256 }))
> # o en bash
> openssl rand -base64 32
> ```

### Setup Rápido con PowerShell

```powershell
# Guardar como setup-dev.ps1
$env:ConnectionStrings__AgendaDb = "Server=localhost;Database=AgendaDb;Trusted_Connection=True;TrustServerCertificate=True"
$env:Jwt__Secret = "mi-clave-secreta-muy-larga-de-al-menos-32-caracteres!!"
$env:OpenAI__ApiKey = "sk-proj-..."
$env:TokenEncryption__MasterKey = [Convert]::ToBase64String(@(1..32))

# Aplicar migrations y correr
dotnet ef database update --project AgendaApi.Infrastructure --startup-project AgendaApi.Api
dotnet run --project AgendaApi.Api
```

---

## Cómo Conectar un Nuevo Tenant

Cada tenant (negocio) pasa por este flujo de onboarding:

```
1. POST /api/tenants                 → Crear el tenant
2. POST /api/tenants/{id}/service-types → Definir tipos de cita
3. POST /api/tenants/{id}/calendar-connection
   └── Elegir proveedor: "google" o "microsoft"
   └── Configurar credenciales OAuth
   └── Definir availability_rules (horarios)
4. Conectar WhatsApp:
   └── Configurar webhook de Meta → POST /api/webhook
   └── Configurar Phone Number ID + Access Token
5. ¡El agente ya puede recibir mensajes!
```

### Ejemplo: Registrar un tenant con Google Calendar

```bash
# 1. Crear tenant
curl -X POST http://localhost:5000/api/tenants \
  -H "Content-Type: application/json" \
  -d '{
    "nombre": "Peluquería Canina",
    "calendarProvider": "google"
  }'

# 2. Agregar tipo de servicio
curl -X POST http://localhost:5000/api/tenants/{tenantId}/service-types \
  -H "Content-Type: application/json" \
  -d '{
    "nombre": "Corte y baño",
    "duracionMinutos": 60,
    "bufferMinutos": 15,
    "precio": 15000
  }'

# 3. Configurar conexión de calendario
curl -X POST http://localhost:5000/api/tenants/{tenantId}/calendar-connection \
  -H "Content-Type: application/json" \
  -d '{
    "accountEmail": "negocio@gmail.com",
    "accessToken": "ya29...",
    "refreshToken": "1//...",
    "tokenExpiresAt": "2026-08-27T00:00:00Z",
    "provider": "google"
  }'
```

---

## API Endpoints

| Método | Ruta | Descripción |
|---|---|---|
| `GET` | `/health` | Health check |
| `GET` | `/api/tenants` | Listar tenants activos |
| `POST` | `/api/tenants` | Crear nuevo tenant |
| `POST` | `/api/tenants/{id}/calendar-connection` | Configurar calendario del tenant |
| `POST` | `/api/tenants/{id}/service-types` | Agregar tipo de servicio |
| `GET` | `/api/appointments/availability` | Consultar disponibilidad |
| `POST` | `/api/appointments` | Crear cita |
| `PUT` | `/api/appointments/{id}/reschedule` | Reprogramar cita |
| `POST` | `/api/appointments/{id}/cancel` | Cancelar cita |
| `GET` | `/api/appointments` | Listar citas del tenant |
| `GET` | `/api/appointments/{id}` | Detalle de cita |
| `GET` | `/api/webhook` | Verificación webhook Meta |
| `POST` | `/api/webhook` | Recibir mensajes WhatsApp |
| `POST` | `/api/webhook/calendar` | Notificaciones de calendario |

---

## Modelo de Datos

```
tenants (id_tenant, nombre, calendar_provider, activo, ...)
  │
  ├── calendar_connections (id, id_tenant, access_token, refresh_token, ...)
  │
  ├── service_types (id, id_tenant, nombre, duracion_minutos, buffer_minutos, ...)
  │
  ├── availability_rules (id, id_tenant, dia_semana, hora_inicio, hora_fin, ...)
  │
  ├── availability_exceptions (id, id_tenant, fecha, dia_completo, ...)
  │
  ├── clients (id, id_tenant, whatsapp, nombre, ...)
  │
  └── appointments (id, id_tenant, id_client, id_service_type,
                    fecha_inicio, fecha_fin, estado, external_event_id, ...)
```

### Patrón Multi-Tenant

Mismo patrón que AdamApi: **tenant_id compartido** (`id_tenant` GUID en cada tabla). No hay schema-per-tenant. Esto simplifica la gestión de migraciones y la administración de la base de datos.

---

## Pipeline de Mensajería WhatsApp

El pipeline replica el diseño probado de AdamApi:

```
Webhook POST de Meta
    ↓
WhatsAppCloudApiAdapter.ParseWebhookPayloadAsync()
    ↓    (parsea JSON → List<IncomingMessage>)
MessageBufferService
    ↓    (buffer de 30s por usuario, dedup, rate-limit, Channel-based)
ChatOrchestratorService
    ↓    (tool-calling loop, max 5 iteraciones)
      → OpenAI (gpt-4o-mini) / Anthropic (claude-3-haiku) fallback
      → Tool Executor (check_availability, create_appointment, etc.)
    ↓
WhatsAppCloudApiAdapter.SendTextAsync()
    ↓    (respuesta al cliente)
```

---

## Próximos Pasos (Implementación Pendiente)

> ✅ **Actualización:** Todos los componentes principales han sido implementados.

### Estado de Implementación

| Componente | Estado | Archivos |
|---|---|---|
| **AI Providers** | ✅ Completado | `OpenAIProvider.cs`, `AnthropicProvider.cs` |
| **Tool Definitions** | ✅ Completado | `AppointmentToolDefinitions.cs` (formato OpenAI + Anthropic) |
| **MessageBufferService** | ✅ Completado | Channel-based, 30s buffer, dedup, rate-limit, cleanup timers |
| **ChatOrchestratorService** | ✅ Completado | Tool-calling loop (5 iteraciones), fallback Anthropic, execution de 5 tools |
| **Google Calendar Adapter** | ✅ Completado | REST API v3, auto-refresh OAuth, GetAvailability, CRUD eventos |
| **Microsoft Graph Adapter** | ✅ Completado | REST API v1.0, auto-refresh OAuth, calendarView, CRUD eventos |
| **OAuth Flow** | ✅ Completado | `GoogleOAuthController.cs`, `MicrosoftOAuthController.cs` |
| **Token Encryption** | ✅ Completado | AES-256-GCM, `ITokenEncryptionService` + `TokenEncryptionService` |
| **ListAppointmentsUseCase** | ✅ Completado | Listado por WhatsApp + filtro por estado |
| **Webhook + Buffer** | ✅ Completado | WebhookController encola en MessageBufferService |

### Pendientes (mejoras futuras)

1. **Webhook de calendario** — Procesar notificaciones push de cambios externos (SyncExternalChangesUseCase)
2. **Delta sync** — Implementar `GetChangesAsync` con sync tokens en ambos adaptadores
3. **Watch/Subscriptions** — Implementar `SubscribeToChangesAsync` para Google Calendar y Microsoft Graph
4. **Métricas y monitoreo** — Agregar métricas de uso de AI, latencia, etc.
5. **Tests** — Unit tests e integration tests
6. **CI/CD** — Pipeline de build y deploy

---

## Licencia

MIT
