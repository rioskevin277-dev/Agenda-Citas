# AgendaApi — Asistente de Citas por WhatsApp (Multi-Tenant)

Agente conversacional con IA que **agenda, consulta, reprograma, confirma y cancela citas** a través
de **WhatsApp**, sincronizadas con **Google Calendar** o **Microsoft 365 (Outlook)**
([conexión](#-conectar-microsoft-365)). Multi-tenant: un mismo sistema sirve a varios negocios,
cada uno con su propio calendario, servicios y horarios.

Además del bot, el sistema incluye un **CRM del dueño** (perfil + historial + conversaciones de cada
cliente), **recordatorios automáticos multi-etapa**, **lista de espera** (FIFO con aviso al liberarse
un cupo), **múltiples profesionales** con su propio horario y cartera de servicios, y un
**dashboard operativo** con los KPIs del negocio.

---

## ⚡ Lo importante (empieza aquí)

### ¿Qué es?
Un **bot de WhatsApp** con inteligencia artificial que atiende a tus clientes: les dice horarios disponibles, les agenda la cita y la crea como evento real en tu Google Calendar. El cliente no habla con una persona, habla con el bot.

### ¿Cómo funciona, en 1 frase?
```
WhatsApp → Meta → tu dominio → túnel → API .NET → IA (Groq→OpenAI→Anthropic) → Google Calendar
```

### ¿Cómo se inicia?
Dos modos:

| Modo | Comando | Para qué |
|---|---|---|
| **Desarrollo local** | `.\run.ps1` (o `setup-dev.ps1`) | Pruebas en tu máquina, sin Docker |
| **Producción** | `.\scripts\start-production.ps1` | Servicio real en tu PC con túnel |

Solo necesitas **.NET 8 SDK** (local) y **un único archivo `.env`** con las claves.

### URLs de producción
- API: `https://api.adamcoia.com`
- Swagger: `https://api.adamcoia.com/swagger`
- Health: `https://api.adamcoia.com/health`

---

## 🧩 ¿Cómo funciona el asistente en detalle?

Cada mensaje de WhatsApp pasa por un **orquestador de IA** (`ChatOrchestratorService`) que decide qué hacer, con un ciclo de *tool-calling*:

1. **Entra el mensaje** del cliente al webhook de WhatsApp.
2. El orquestador carga el **contexto del negocio**: servicios reales, horarios, historial de la conversación (memoria de 24 h).
3. Pide al modelo de IA una respuesta. El modelo puede **llamar una herramienta** (o varias) en lugar de responder.
4. El sistema **ejecuta la herramienta** y devuelve el resultado al modelo.
5. El modelo **redacta la respuesta final** y se envía por WhatsApp.

### Herramientas disponibles (las acciones que el bot sabe hacer)

| Herramienta | Qué hace |
|---|---|
| `check_availability` | Verifica horarios libres en el **calendario real** (Google) + reglas del negocio |
| `create_appointment` | Agenda una cita y crea el evento en Google Calendar |
| `cancel_appointment` | Cancela una cita (local + Google) |
| `confirm_appointment` | Marca una cita como confirmada (responde al CONFIRMAR) |
| `reschedule_appointment` | Reprograma a otra fecha/hora (identifica por WhatsApp o ID) |
| `list_appointments` | Lista las citas del cliente |
| `add_to_waitlist` | Apunta al cliente a la lista de espera de un servicio (si no hay cupo) |

> **Importante**: la IA **solo agenda servicios que existen** en tu negocio. Si un cliente pide algo que no está en la lista, el bot responde que no está disponible en vez de inventar.

### Proveedores de IA (cadena de respaldo)
El sistema prueba en orden hasta que uno responde:
1. **Groq** (gratuito, rápido) — principal
2. **OpenAI** — respaldo
3. **Anthropic** — segundo respaldo

### Memoria de conversación
Recuerda el contexto de cada cliente durante **24 horas** (saludo, servicios que pidió, citas en curso). Al expirar, empieza de cero.

---

## ⏰ Recordatorios automáticos (multi-etapa, por negocio)

Un servicio en segundo plano (`ReminderBackgroundService`, ciclo de 5 min) envía recordatorios
por WhatsApp según la configuración **de cada negocio (tenant)**:

- `recordatorio_habilitado` — activa/desactiva los recordatorios (default `true`).
- `recordatorio_1_horas` — antelación de la 1ª etapa en horas (default **24**). `0` = sin etapa.
- `recordatorio_2_horas` — antelación de la 2ª etapa en horas (default **2**). `0` = sin etapa.

Ejemplo: **Tenant A** 24h + 2h → aviso "tienes una cita mañana…" (pedir confirmar) y, si aún no
confirma, un nudge final 2h antes. **Tenant B** puede usar solo 48h.

```
⏰ Recordatorio: tienes una cita PENDIENTE de confirmación para el 07/08/2026 a las 14:00.
Responde CONFIRMAR para confirmarla, CANCELAR para cancelarla o REAGENDAR para cambiar la fecha.
```

- **CONFIRMAR** → marca la cita como `confirmed` (`confirm_appointment`).
- **CANCELAR** → la cancela y acaba el turno (no se reintenta).
- **REAGENDAR** → el bot pregunta la nueva fecha, verifica disponibilidad y la mueve.

La **2ª etapa solo se envía a citas aún no confirmadas**. Al reagendar no hay que hacer nada:
las ventanas de recordatorio se recalculan solas contra la nueva fecha, y una etapa ya enviada
no se repite.

**Entrega y reintentos:** cada intento se registra en `reminder_logs` (cita, etapa, estado
`sent`/`delivered`/`failed`, reintentos, wamid). Los fallos se reintentan hasta 3 veces; el
callback de estado de Meta actualiza la entrega (`delivered`/`failed`).

**Templates:** fuera de la ventana de sesión de 24h, WhatsApp rechaza el texto libre (131047).
Para enviar en cualquier momento, se usan **templates aprobados** configurados por env
(`WhatsApp__RecordatorioTemplate24h` / `WhatsApp__RecordatorioTemplate2h`). Si no hay template,
se envía texto libre solo dentro de la ventana de sesión.

---

## 🚀 Inicio Rápido — Desarrollo Local

### Requisitos
- **.NET 8 SDK** ([descargar](https://dotnet.microsoft.com/download))
- **SQL Server** (Docker `docker compose up -d sqlserver` o instalación local)
- Un **`.env`** en la raíz del proyecto

### 1. Clonar
```bash
git clone https://github.com/rioskevin277-dev/Agenda-Citas.git
cd agenda-api
```

### 2. Crear el `.env`
```bash
copy .env.example .env   # o crea el archivo desde la tabla de abajo
```

### 3. Iniciar
```powershell
# Opción recomendada — carga .env, migra y corre todo:
.\run.ps1

# O manualmente:
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project AgendaApi.Api
```

### 4. Probar
```powershell
curl.exe http://localhost:5000/health                      # → {"status":"healthy"}
curl.exe http://localhost:5000/swagger                     # documentación interactiva
.\scripts\test-webhook.ps1 -Message "Quiero agendar una cita"   # simula un cliente por WhatsApp
.\scripts\test-full-flow.ps1                                # flujo completo automatizado
```

---

## 🔑 Configuración (el archivo `.env`)

> ⚠️ **`.env` NO se sube a git** (está en `.gitignore`). Contiene secretos reales. Nunca lo publiques.

| Variable | Obligatorio | ¿Qué es? / Dónde obtenerla |
|---|---|---|
| `GROQ_API_KEY` | ✅ | API key de [Groq](https://console.groq.com). **Provider principal** |
| `OPENAI_API_KEY` | ⚠️ | En [platform.openai.com](https://platform.openai.com/api-keys). Respaldo (debe ser una key real, no `sk-xxx`) |
| `ANTHROPIC_API_KEY` | ❌ | Respaldo. [console.anthropic.com](https://console.anthropic.com) |
| `ConnectionStrings__AgendaDb` | ✅ | Cadena de conexión SQL Server |
| `JWT_SECRET` | ✅ | ≥32 caracteres. Firmado JWT |
| `MASTER_KEY` | ✅ | Clave AES-256 para cifrar tokens OAuth. Generar `openssl rand -base64 32` |
| `WHATSAPP_ACCESS_TOKEN` | ⚠️ | Token de Meta Cloud API |
| `WHATSAPP_PHONE_NUMBER_ID` | ⚠️ | ID del número de WhatsApp en Meta |
| `WHATSAPP_VERIFY_TOKEN` | ⚠️ | Token de verificación del webhook |
| `GoogleOAuth__ClientId` | ⚠️ | Requisito ENABLED **Google Calendar sync** |
| `GoogleOAuth__ClientSecret` | ⚠️ | Requisito ENABLED **Google Calendar sync** |
| `MicrosoftOAuth__ClientId` | ⚠️ | Requisito ENABLED **Microsoft 365 sync** (ver [Conectar Microsoft 365](#-conectar-microsoft-365)) |
| `MicrosoftOAuth__ClientSecret` | ⚠️ | Requisito ENABLED **Microsoft 365 sync** |
| `PUBLIC_BASE_URL` | ⚠️ | Dominio/túnel público de la API. Arma la `notificationUrl` de las suscripciones de Google/Microsoft |
| `Calendar__TimeZone` | ❌ | Zona horaria del negocio. Default `America/Bogota` |

> **Esquema de nombres**: el código lee las variables con formato `Section__Key` (`Groq__ApiKey`, `OpenAI__ApiKey`, `WhatsApp__AccessToken`, `GoogleOAuth__ClientId`, `MicrosoftOAuth__ClientId`, `ConnectionStrings__AgendaDb`, …). `.env` debe usar **exactamente** esos nombres (`run.ps1` los setea literal como variables de entorno). Las variantes `ALL_CAPS` (`GROQ_API_KEY`, `GOOGLE_OAUTH_CLIENT_ID`, …) son legacy y no se leen.

---

## 🔵 Conectar Microsoft 365

Sincroniza las citas con **Outlook Calendar** vía **Microsoft Graph API** (flujo OAuth delegado). Un tenant usa **un solo** proveedor de calendario (`tenants.calendar_provider = "google"` o `"microsoft"`); el de Google funciona igual salvo la sección de registro.

### 1. Registrar la app en Microsoft Entra

1. Entrar a **[entra.microsoft.com](https://entra.microsoft.com)** → **App registrations** → **New registration**.
2. Nombre: ej. `AgendaApi`. **Accounts in this organizational directory only** (o *Any directory* si querés cuentas personales Outlook; el `common` dels authorize usa multi-tenant).
3. **Redirect URI**: plataforma **Web** → `https://<tu-dominio>/api/v1/oauth/microsoft/callback` (en local, el dominio del túnel; ej. `https://api.adamcoia.com/...`).
4. **Certificates & secrets** → **New client secret** → guardar el valor (solo se muestra una vez) en `MicrosoftOAuth__ClientSecret`.
5. **API permissions** → **Add a permission** → **Microsoft Graph** → **Delegated permissions** → marcar:
   - `Calendars.ReadWrite`
   - `offline_access` (para obtener refresh token)
   - Aceptar **Grant admin consent** (solo una vez).
6. El `Client ID` (Application ID) de la página de **Overview** va en `MicrosoftOAuth__ClientId`.

> Token/refresh en caliente: la app guarda los tokens de fecha cifrados (AES-256) en `calendar_connections`, y el access token se renueva automáticamente con el refresh token cuando expira.

### 2. Configuración `.env`

```env
MicrosoftOAuth__ClientId=<Application (client) ID>
MicrosoftOAuth__ClientSecret=<valor del client secret>
PUBLIC_BASE_URL=https://api.adamcoia.com   # dominio/túnel público (webhooks)
Calendar__TimeZone=America/Bogota          # zona horaria del negocio
```

### 3. Conectar un tenant

Con la API arriba (`dotnet run` o `scripts/start-production.ps1`):

1. **Crear el tenant** (si no existe): `POST /api/v1/tenants` (o reusar uno existente) → anotar el `idTenant`.
2. **Iniciar el OAuth** en el navegador:
   `GET https://<dominio>/api/v1/oauth/microsoft/authorize?tenantId=<idTenant>`
3. Iniciar sesión con la cuenta de Outlook, aceptar los permisos y el callback guarda la conexión. En la respuesta sale `{ "message": "Microsoft 365 Calendar conectado exitosamente", "accountEmail": "…" }`.
4. A partir de ahí el tenant agenda/consulta/cancela en Outlook. El webhook (`POST /api/v1/webhook/calendar`) queda **suscrito y auto-renovado** cada hora, y sincroniza los cambios hechos a mano en el calendario.

---

## 🗄️ Base de datos (¿dónde se gestiona la información?)

**SQL Server 2022** con **EF Core** (Code-First, migraciones automáticas al iniciar). Un solo esquema `dbo` con tabla por entidad (**snake_case**), y **multi-tenant** mediante una columna `id_tenant` en cada tabla.

| Tabla | Guarda |
|---|---|
| `tenants` | Cada negocio/usuario. **Aquí se configura todo**: `calendar_provider`, `whatsapp_phone_number_id`, horarios |
| `clients` | **Información del cliente**: nombre, WhatsApp, email + perfil CRM: `estado` (`nuevo/frecuente/inactivo/no_show/vip/blacklist`), `tags`, `proxima_cita`. Se crea/actualiza al primera vez que escribe |
| `service_types` | Servicios del negocio (Consulta General, Procedimiento Menor…) con duración, buffer y precio |
| `professionals` | **Profesionales** del negocio: nombre, canal por profesional, horario personal |
| `professional_services` | Cartera: qué **servicios** presta **cada profesional** |
| `availability_rules` | **Horarios de atención** por día de la semana (ej. Lun–Vie 09:00–18:00), por negocio o por profesional |
| `availability_exceptions` | Excepciones puntuales de disponibilidad |
| `appointments` | Las citas: fecha inicio/fin, estado (`pending/confirmed/cancelled/…`), `external_event_id` (ID del evento en calendario), recordatorio |
| `calendar_connections` | Tokens OAuth de cada cliente+calendario, **cifrados (AES-256)** |
| `reminder_logs` | Registro de cada recordatorio: etapa, estado (`sent/delivered/failed`), reintentos, `wamid` |
| `conversation_messages` | **Historial durable** de los mensajes de conversación por cliente (CRM) |
| `waitlist_entries` | **Lista de espera** FIFO por servicio/profesional: entrada activa, cumplida, expirada (7 días) |

### Datos de ejemplo (seed)
`scripts/seed-tenant-data.sql` llena `service_types`, `availability_rules` y los **profesionales** del
tenant de prueba (Dra. María con horario personal lun–vie 09–17 y Dr. Carlos, ambos con su cartera de
los 3 servicios). Ejecútalo con `sqlcmd` contra el contenedor de SQL Server (ver "Acceso a la base" en `scripts/start-production.ps1`).

---

## 🌐 Producción

### Topología (sin IP pública, sin abrir puertos)

```
Meta (WhatsApp) ──HTTPS──> api.adamcoia.com ──> Cloudflare Tunnel ──> localhost:8080 ──> API .NET
                                                          └──────────────── Docker (SQL Server + API)
```

| Dato | Valor |
|---|---|
| **URL** | `https://api.adamcoia.com` |
| **Servidor** | PC local (Windows 11 Home) |
| **Virtualización** | WSL2 + Ubuntu 24.04 |
| **Contenedores** | Docker Desktop |
| **Base de datos** | SQL Server 2022 Express (contenedor `agenda-sqlserver`) |
| **API** | .NET 8 (contenedor `agenda-api`) |
| **Túnel** | Cloudflare Tunnel |
| **DNS** | Cloudflare |

### ⚠️ Gotcha de build (leer antes de desplegar)
El `Dockerfile` **copia `publish_local/`** (el binario publicado), NO compila desde el código. Tras cada cambio:

```bash
# 1) Publicar el binario
dotnet publish AgendaApi.Api/AgendaApi.Api.csproj -c Release -o publish_local

# 2) Reconstruir el contenedor con el binario nuevo
docker compose up -d --build api
```

Si publicas sin el paso 1, el contenedor corre **código viejo** aunque el `--build` diga éxito.

### 🌥️ Despliegue en la nube (VPS Linux — recomendado para producción)

En un VPS Ubuntu 24.04 con un dominio propio, el desarrollador desplega todo de una sola vez:

```bash
sudo ./deploy/setup-server.sh api.tuempresa.com
# luego editar /opt/agenda-api/.env con las claves reales y:
cd /opt/agenda-api/app && docker compose restart api
```

El script instala Docker, genera claves seguras, clona el repo, compila la imagen
(multi-etapa, sin publicar binario a mano), levanta SQL Server + API y configura
Nginx + Let's Encrypt (HTTPS). Solo falta re-apuntar al dominio nuevo los webhooks
de WhatsApp/Google/M365.

> El `Dockerfile` es **multi-etapa y compila desde el código**: en local y en la
> nube, `docker compose up --build` basta. Ya **no** se requiere `dotnet publish`.
> (Nota para desarrolladores: la imagen empuja los `.env` por el `--env-file` de
> compose; la conexión `Server=sqlserver` no cambia entre local y nube.)

### Iniciar producción (un solo comando — recomendado)

Hace **todo** el ciclo: publica el binario nuevo, reconstruye y levanta los
contenedores, asegura Docker y el túnel de Cloudflare, y verifica el health
real del dominio público. Esto evita el error clásico de correr contenedores
con código viejo por no haber publicado antes.

```powershell
.\deploy.ps1            # o .\deploy.bat con doble clic
.\scripts\deploy-production.ps1   # el script completo
```

### Iniciar producción (solo levantar, sin re-publicar)

```powershell
.\scripts\start-production.ps1    # Docker + SQL Server + API + túnel
```
> ⚠️ Este NO re-publica el binario. Si cambiaste código, usa `.\deploy.ps1`.

O manualmente desde WSL:
```bash
cd /mnt/c/Users/USUARIO/agenda-api
docker compose up -d
cloudflared tunnel run agenda-api
```

### Configurar el calendario en producción (Google Calendar
1. Crea un **OAuth Client en Google Cloud** con redirect exacto `https://api.adamcoia.com/api/v1/oauth/google/callback`.
2. Agenda las credenciales en `.env` (`GOOGLE_OAUTH_CLIENT_*`), re-publica y corre.
3. Visita `https://api.adamcoia.com/api/v1/oauth/google/authorize?tenantId=<id-tenant>` y autoriza.
4. El token queda guardado **cifrado** en `calendar_connections`.

---

## 🔌 API (endpoints)

Todas las rutas de negocio van bajo `api/v1` y requieren **JWT Bearer** (`[Authorize]`); el tenant se
resuelve del claim `IdTenant`.

| Método | Ruta | Descripción |
|---|---|---|
| `GET` | `/health` | Health check (público) |
| `GET/POST` | `/api/v1/webhook` | Webhook **WhatsApp** (GET = verificación Meta, POST = mensajes entrantes) |
| `POST` | `/api/v1/webhook/calendar` | Webhook **calendario** (suscripción Google/Microsoft, cambios a mano) |
| `GET` | `/api/v1/tenants` | Listar tenants |
| `POST` | `/api/v1/tenants` | Crear tenant |
| `POST` | `/api/v1/tenants/{id}/calendar-connection` | Conectar calendario |
| `GET/POST` | `/api/v1/tenants/{id}/service-types` | Servicios del tenant |
| `GET/POST` | `/api/v1/tenants/{id}/professionals` | Profesionales del tenant |
| `POST` | `/api/v1/tenants/{id}/professionals/{pid}/services` | Asignar servicios a un profesional |
| `GET` | `/api/v1/clients?q=` | **CRM**: listar clientes (filtro por nombre/WhatsApp) |
| `GET` | `/api/v1/clients/{id}` | **CRM**: detalle (perfil + historial + conversaciones) |
| `PUT` | `/api/v1/clients/{id}` | **CRM**: editar perfil (estado/tags/notas) |
| `GET` | `/api/v1/appointments/availability` | Ver disponibilidad |
| `POST` | `/api/v1/appointments` | Crear cita |
| `GET` | `/api/v1/appointments` | Listar citas |
| `GET` | `/api/v1/appointments/{id}` | Detalle de una cita |
| `POST` | `/api/v1/appointments/{id}/cancel` | Cancelar |
| `PUT` | `/api/v1/appointments/{id}/reschedule` | Reprogramar |
| `GET` | `/api/v1/dashboard/summary` | **Dashboard**: KPIs operativos (`fechaDesde/fechaHasta` opcionales) |
| `GET` | `/api/v1/oauth/microsoft/authorize` · `/callback` | OAuth **Microsoft 365** |
| `GET` | `/api/v1/oauth/google/authorize` · `/callback` | OAuth **Google Calendar** |

Documentación interactiva completa en `/swagger`.

---

## 🏗️ Arquitectura (Arquitectura Limpia)

```
┌──────────────────────────────────────────────────────────────┐
│                    AgendaApi.Api (Web API)                    │
│  Controllers · OAuth · Webhook · Middleware · Program.cs     │
├──────────────────────────────────────────────────────────────┤
│              AgendaApi.Application (Use Cases + DTOs)         │
│  CheckAvailability · Create· Cancel· Confirm· Reschedule...  │
├──────────────────────────────────────────────────────────────┤
│              AgendaApi.Domain (Core + Ports)                  │
│  Entities (Tenant, Appointment, Client, ServiceType...)      │
│  Ports/interfaces: ICalendarProvider, IMessagingProvider,     │
│                     repositorios (IUnitOfWork)               │
├──────────────────────────────────────────────────────────────┤
│          AgendaApi.Infrastructure (Adapters)                  │
│  GoogleCalendarAdapter · MicrosoftGraphCalendarAdapter         │
│  WhatsAppCloudApiAdapter · EF Core · AI providers (Groq,     │
│  OpenAI, Anthropic) · Repositorios EF                       │
└──────────────────────────────────────────────────────────────┘
```

### Stack tecnológico

| Capa | Tecnología |
|---|---|
| Lenguaje | C# 12 (.NET 8) |
| Base de datos | SQL Server 2022 + EF Core 8 |
| Mensajería | WhatsApp Cloud API (Meta) |
| Calendario | Google Calendar API v3 / Microsoft Graph |
| IA (tool-calling) | Groq (principal) → OpenAI → Anthropic |
| Auth | JWT Bearer |
| Almacén de tokens | Cifrado AES-256 (tokens OAuth) |
| Documentación | Swagger / OpenAPI |
| Deploy | Docker Compose + WSL2 + Cloudflare Tunnel |

### Zona horaria
Las citas se guardan en hora local del negocio **disfrazada de UTC** (Google Calendar trabaja con UTC real). Por eso hay conversión en la frontera (`Calendar__TimeZone`, default `America/Bogota`). Esto se maneja dentro del adaptador de calendario.

---

## 📦 Estructura del proyecto

```
AgendaApi.sln
├── AgendaApi.Domain/          # Entidades + interfaces (puertos)
├── AgendaApi.Application/     # Casos de uso + DTOs
├── AgendaApi.Infrastructure/  # Adaptadores (EF Core, Google, WhatsApp, AI)
├── AgendaApi.Api/             # Host web (controllers, OAuth, webhook)
├── AgendaApi.Tests/           # Tests unitarios (170 tests)
├── deploy/                    # Scripts de deploy
├── scripts/                   # Scripts (start, seed, test webhook/full flow)
├── docker-compose.yml         # Orquestación (sqlserver + api)
├── Dockerfile                 # Copia publish_local/ (ver gotcha arriba)
├── run.ps1                    # Inicio local (carga .env + dotnet run)
├── .env                       # Único archivo de configuración (NO a git)
└── publish_local/              # Binario publicado (gitignored)
```

---

## 🧪 Testing

```bash
dotnet test AgendaApi.sln
# 170/170 superados
```

Cubre casos de uso clave: disponibilidad, creación/cancelación/reprogramación/confirmación de citas,
lista de espera, recordatorios multi-etapa, clientes y dashboard.

---

## 🛠️ Solución de problemas

**El bot responde “Lo siento, tuve un problema”**
- La cadena de IA falló por completo:
  - `OPENAI_API_KEY` está en `sk-xxx` (placeholder) → pon una real.
  - No hay `GROQ_API_KEY`/`ANTHROPIC_API_KEY` configurada.
  - El proveedor se quedó sin herramienta para la acción (ej. confirmar una cita).

**Un servicio que pidió no existe**
- La IA agrega solo los `service_types` reales del tenant. Revisa la tabla `service_types` para ese `id_tenant` (seed con `scripts/seed-tenant-data.sql`).

**El recordatorio llega apenas se agenda**
- La ventana es de 4 h. Si llegó justo después de agendar, es que la cita está a <4h de distancia (normal).

**Los eventos de Google aparecen 5 horas corridos**
- Problema de zona horaria en la frontera. Revisa que se use la lógica `FromGoogleInstant` / `LocalDateTimeToGoogleIso` en el adaptador (ya corregido).

**`docker compose up` construye pero el cambio no aparece**
- Vuelve a `dotnet publish -c Release -o publish_local` y después `docker compose up -d --build api`.

---

## ⚠️ Buenas prácticas de seguridad
- **Nunca** subas `.env` o lo pegues en chats/README. Contiene: client secrets OAuth, JWT, claves de IA, contraseña SQL.
- `.gitignore` debe incluir `.env` y `publish_local/`.
- Rota las claves si alguna se filtró. Cambia `MASTER_KEY`/`JWT_SECRET` en **producción** (no uses las de ejemplo).

---

## 🔗 Repositorio
https://github.com/rioskevin277-dev/Agenda-Citas