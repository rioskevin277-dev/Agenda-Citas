# AgendaApi — Agente de Citas Multi-Tenant por WhatsApp

Agente conversacional multi-tenant que agenda citas en calendario a través de **WhatsApp**, con integración a **Google Calendar** y **Microsoft 365**.

---

## 🌐 Producción

| Dato | Valor |
|---|---|
| **URL** | `https://api.adamcoia.com` |
| **Swagger** | `https://api.adamcoia.com/swagger` |
| **Health** | `https://api.adamcoia.com/health` |
| **Servidor** | PC local (Windows 11 Home) |
| **Virtualización** | WSL2 + Ubuntu 24.04 |
| **Contenedores** | Docker Desktop |
| **Base de datos** | SQL Server 2022 Express (Docker) |
| **Túnel** | Cloudflare Tunnel (→ `api.adamcoia.com`) |
| **DNS** | Cloudflare (Free) |
| **Repositorio** | [github.com/rioskevin277-dev/Agenda-Citas](https://github.com/rioskevin277-dev/Agenda-Citas) |

### ¿Cómo funciona?
```
Usuario WhatsApp → Meta → https://api.adamcoia.com → Cloudflare Tunnel → localhost:8080 → API .NET
```
Sin IP pública, sin abrir puertos, 100% gratis.

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

## 🚀 Primeros Pasos (Local)

```bash
# 1. Clonar
git clone https://github.com/rioskevin277-dev/Agenda-Citas.git
cd agenda-api

# 2. Restaurar
dotnet restore

# 3. Configurar variables de entorno (ver abajo)

# 4. Migraciones
dotnet ef database update --project AgendaApi.Infrastructure --startup-project AgendaApi.Api

# 5. Correr
dotnet run --project AgendaApi.Api
# → http://localhost:5000/swagger
```

### Variables de Entorno

| Variable | ¿Qué es? |
|---|---|
| `ConnectionStrings__AgendaDb` | Conexión a SQL Server |
| `Jwt__Secret` | Clave JWT (mín. 32 caracteres) |
| `OpenAI__ApiKey` | API Key de OpenAI |
| `TokenEncryption__MasterKey` | Clave AES-256 en Base64 (generar con `openssl rand -base64 32`) |
| `WhatsApp__AccessToken` | Token de WhatsApp Cloud API |
| `WhatsApp__PhoneNumberId` | ID del número de WhatsApp |
| `WhatsApp__VerifyToken` | Token de verificación del webhook |

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

## 🐳 Producción (Docker)

Para levantar en producción local:

```powershell
.\scripts\start-production.ps1
```

O manualmente desde WSL Ubuntu:
```bash
cd /mnt/c/Users/USUARIO/agenda-api
docker compose up -d
cloudflared tunnel run agenda-api
```

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
└── docker-compose.yml         # Orquestación Docker
```

---

## 📝 Notas

- **Multi-tenant**: Un solo schema, `id_tenant` GUID en cada tabla
- **Cifrado**: Tokens OAuth cifrados con AES-256-GCM
- **Rate limiting**: Buffer de 30s por usuario + dedup de mensajes
- **Webhook WhatsApp**: Verificación mediante `WhatsApp__VerifyToken`
