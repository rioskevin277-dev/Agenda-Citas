# AgendaApi — Agente de Citas Multi-Tenant por WhatsApp

Agente conversacional multi-tenant que agenda citas en calendario a través de **WhatsApp**, con integración a **Google Calendar** y **Microsoft 365**.

---

## ⚡ Inicio Rápido (Desarrollo Local)

Solo necesitas **un archivo `.env`** y **.NET 8 SDK**.

### 1. Clonar

```bash
git clone https://github.com/rioskevin277-dev/Agenda-Citas.git
cd agenda-api
```

### 2. Configurar (un solo `.env`)

Edita `C:\Users\USUARIO\agenda-api\.env` con tus datos:

| Variable | Obligatorio | Dónde obtenerla |
|---|---|---|
| `OPENAI_API_KEY` | ✅ Sí | [platform.openai.com/api-keys](https://platform.openai.com/api-keys) |
| `ConnectionStrings__AgendaDb` | ✅ Sí | Cadena de conexión a SQL Server (ver abajo) |
| `JWT_SECRET` | ✅ Sí | Cualquier texto de ≥32 caracteres |
| `MASTER_KEY` | ✅ Sí | `openssl rand -base64 32` o usa la que viene por defecto |
| `ANTHROPIC_API_KEY` | ❌ No | Fallback si OpenAI falla |
| `WHATSAPP_*` | ❌ No | Solo para probar webhooks reales |

> **SQL Server**: Puedes usar Docker (`docker compose up -d sqlserver`) o una instalación local.
> Para SQL Server local con autenticación de Windows, cambia la línea en `.env`:
> ```env
> ConnectionStrings__AgendaDb=Server=localhost;Database=AgendaApi;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true
> ```

### 3. Iniciar

```powershell
# Opción recomendada — carga .env y corre todo:
.\run.ps1

# O manualmente:
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project AgendaApi.Api
```

La API arranca en `http://localhost:5000` — **solo local, sin exponer nada**.

```
📄 Cargando .env...
   ✅ 20 variables cargadas
📦 Restaurando paquetes...
📦 Aplicando migraciones...
═══════════════════════════════════════════
   🚀 Iniciando AgendaApi...
═══════════════════════════════════════════
   Swagger:  http://localhost:5000/swagger
   Health:   http://localhost:5000/health
```

---

## 🧪 Cómo Probar

### Health check

```powershell
curl.exe http://localhost:5000/health
# → {"status":"healthy","timestamp":"...}
```

### Swagger (navegador)

Abrir `http://localhost:5000/swagger` — documentación interactiva de todos los endpoints.

### Crear un tenant de prueba

```powershell
curl.exe -X POST http://localhost:5000/api/tenants `
  -H "Content-Type: application/json" `
  -d '{"nombre":"Peluquería Canina Test","calendarProvider":"google"}'
```

### Listar tenants

```powershell
curl.exe http://localhost:5000/api/tenants
```

### Simular un webhook de WhatsApp (sin WhatsApp real)

```powershell
.\scripts\test-webhook.ps1 -Message "Quiero agendar una cita"
```

### Flujo completo automatizado

```powershell
.\scripts\test-full-flow.ps1
```

Crea un tenant, agrega un servicio, simula un webhook y muestra los resultados.

---

## 🌐 Producción

| Dato | Valor |
|---|---|
| **URL** | `https://api.adamcoia.com` |
| **Swagger** | `https://api.adamcoia.com/swagger` |
| **Servidor** | PC local (Windows 11 Home) |
| **Virtualización** | WSL2 + Ubuntu 24.04 |
| **Contenedores** | Docker Desktop |
| **Base de datos** | SQL Server 2022 Express (Docker) |
| **Túnel** | Cloudflare Tunnel (→ `api.adamcoia.com`) |
| **DNS** | Cloudflare (Free) |
| **Repositorio** | [github.com/rioskevin277-dev/Agenda-Citas](https://github.com/rioskevin277-dev/Agenda-Citas) |

### ¿Cómo funciona?

```
Usuario WhatsApp → Meta → api.adamcoia.com → Cloudflare Tunnel → localhost:8080 → API .NET
```

Sin IP pública, sin abrir puertos, 100% gratis.

### Iniciar producción

```powershell
.\scripts\start-production.ps1
```

O manualmente desde WSL:
```bash
cd /mnt/c/Users/USUARIO/agenda-api
docker compose up -d
cloudflared tunnel run agenda-api
```

---

## 🏗️ Arquitectura

```
┌──────────────────────────────────────────────────────────────┐
│                    AgendaApi.Api (Web API)                    │
│  Controllers · Middleware · Program.cs · Swagger             │
├──────────────────────────────────────────────────────────────┤
│              AgendaApi.Application (Use Cases)               │
│  CheckAvailability · CreateAppointment · CancelAppointment   │
├──────────────────────────────────────────────────────────────┤
│              AgendaApi.Domain (Core + Ports)                  │
│  Entities: Tenant, Appointment, Client, ServiceType, ...     │
│  Ports: ICalendarProvider, IMessagingProvider, repositorios  │
├──────────────────────────────────────────────────────────────┤
│          AgendaApi.Infrastructure (Adapters)                  │
│  GoogleCalendarAdapter · MicrosoftGraphCalendarAdapter        │
│  WhatsAppCloudApiAdapter · EF Core · Repositorios            │
└──────────────────────────────────────────────────────────────┘
```

### Stack

| Capa | Tecnología |
|---|---|
| **Lenguaje** | C# 12 (.NET 8) |
| **Base de datos** | SQL Server 2022 + EF Core 8 |
| **Mensajería** | WhatsApp Cloud API (Meta) |
| **Calendario** | Google Calendar API v3 / Microsoft Graph API |
| **AI** | OpenAI GPT-4o-mini (+ Anthropic fallback) |
| **Auth** | JWT Bearer |
| **Logging** | Serilog |
| **Documentación** | Swagger / OpenAPI |

---

## 📋 Endpoints Principales

| Método | Ruta | Descripción |
|---|---|---|
| `GET` | `/health` | Health check |
| `GET` | `/api/tenants` | Listar tenants |
| `POST` | `/api/tenants` | Crear tenant |
| `POST` | `/api/tenants/{id}/calendar-connection` | Conectar calendario |
| `POST` | `/api/tenants/{id}/service-types` | Agregar servicio |
| `GET` | `/api/appointments/availability` | Ver disponibilidad |
| `POST` | `/api/appointments` | Crear cita |
| `PUT` | `/api/appointments/{id}/reschedule` | Reprogramar |
| `POST` | `/api/appointments/{id}/cancel` | Cancelar |
| `GET/POST` | `/api/webhook` | Webhook WhatsApp |

---

## 📦 Estructura del Proyecto

```
AgendaApi.sln
├── AgendaApi.Domain/          # Entidades + interfaces (puertos)
├── AgendaApi.Application/     # Casos de uso
├── AgendaApi.Infrastructure/  # Adaptadores (EF Core, APIs externas)
├── AgendaApi.Api/             # Host web (controllers, middleware)
├── AgendaApi.Tests/           # Tests unitarios
├── deploy/                    # Scripts de deploy
├── scripts/                   # Scripts de utilidad
├── docker-compose.yml         # Orquestación Docker
├── run.ps1                    # Inicio local (carga .env + dotnet run)
└── .env                       # Único archivo de configuración
```

---

## 📝 Notas

- **Multi-tenant**: Un solo schema, `id_tenant` GUID en cada tabla
- **Cifrado**: Tokens OAuth cifrados con AES-256-GCM
- **Rate limiting**: Buffer de 30s por usuario + dedup de mensajes
- **Webhook WhatsApp**: Verificación mediante `WHATSAPP_VERIFY_TOKEN`
- **Configuración**: Todo en un solo `.env` — compatible con Docker y desarrollo local
- **Sin exponer**: Por defecto solo escucha en `localhost` — ningún endpoint es público
