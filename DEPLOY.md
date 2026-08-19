# 🚀 Despliegue en producción — Guía para el equipo

Este documento es la **guía de entrega** para dejar el servicio **siempre activo**
en el hosting del equipo. El código se recibe por **git (GitHub)** y el servicio
queda en **contenedores Docker** que se mantienen solos (`restart: always`).

---

## 1. Qué necesitas en el servidor (una sola vez)

- **Ubuntu 24.04 LTS** con **mínimo 2 GB de RAM** (SQL Server Express exige ~2 GB).
- **Docker + Docker Compose plugin**.
- **Dominio propio** para las URLs públicas (`api.tudominio.com`).
- Acceso SSH del usuario de despliegue con permisos de `docker`.

> El código se compila en el propio servidor (Dockerfile multi-etapa). No se
> necesita .NET SDK ni publicar binarios a mano.

## 2. Despliegue manual inicial (opcional, si no usas CI)

```bash
# 1. Clonar
cd /opt/agenda-api
git clone https://github.com/rioskevin277-dev/Agenda-Citas.git app
cd app

# 2. Crear el .env (ver sección 3) y luego:
docker compose --env-file ../.env up -d --build
```

Si prefieres el asistente automático (instala Docker, genera claves, Nginx y
Let's Encrypt de una vez), en el servidor:

```bash
sudo bash deploy/setup-server.sh api.tudominio.com
```

## 3. El archivo `.env` del servidor (IMPORTANTE)

`docker compose` lee las claves de un `.env`. **Nunca subas este archivo a git**.
Crea `/opt/agenda-api/.env` con estas claves (pide los valores reales al dueño
por **canal seguro**, jamás por chat/repositorio):

```env
# Base de datos (docker compose arma la cadena con esto)
SQL_PASSWORD=<clave segura>

# Seguridad (>=32 caracteres)
JWT_SECRET=<generar: openssl rand -base64 32>
MASTER_KEY=<generar: openssl rand -base64 32>

# IA (principal + respaldos)
GROQ_API_KEY=<real>
OpenRouter__ApiKey=<opcional>

# WhatsApp Cloud API (Meta)
WHATSAPP_ACCESS_TOKEN=<token real>
WHATSAPP_PHONE_NUMBER_ID=<id del número>
WHATSAPP_VERIFY_TOKEN=<tu token de verificación>
NOTIFICACIONES_WHATSAPP_DUENO=<número del dueño, con país>
WHATSAPP_RECORDATORIO_TEMPLATE_24H=<template aprobado, opcional>
WHATSAPP_RECORDATORIO_TEMPLATE_2H=<template aprobado, opcional>

# Google Calendar (opcional; redirect en tu dominio)
GoogleOAuth__ClientId=
GoogleOAuth__ClientSecret=

# Microsoft 365 / Outlook (opcional)
MicrosoftOAuth__ClientId=
MicrosoftOAuth__ClientSecret=

# Dominio público y zona horaria
PUBLIC_BASE_URL=https://api.tudominio.com
Calendar__TimeZone=America/Bogota
```

> El `PUBLIC_BASE_URL` y las URL de callback de OAuth deben coincidir con el
> **dominio del producción**, no con el de desarrollo.

## 4. Despliegue automático (CI/CD) — recomendado

Ya está el flujo `.github/workflows/deploy.yml`: en **GitHub → Settings →
Secrets → Actions** agrega:

| Secret | Valor |
|---|---|
| `DEPLOY_HOST` | IP o dominio del servidor |
| `DEPLOY_USER` | usuario SSH con permisos de `docker` |
| `DEPLOY_SSH_KEY` | clave privada SSH de ese usuario |

A partir de ahí, **cada `push` a `main`** hace: CI (test+build) → SSH al servidor
→ `git pull` → `docker compose up -d --build`. El servicio queda actualizado y
vigente sin tocar nada.

## 5. Después de desplegar (obligatorio, una sola vez por dominio)

1. **WhatsApp Webhook**: en Meta Cloud API, apunta el webhook a
   `https://api.tudominio.com/api/v1/webhook` con el `WHATSAPP_VERIFY_TOKEN` y
   vuelve a **verificar**.
2. **Google / Microsoft OAuth**: registra la URL de callback
   `https://api.tudominio.com/api/v1/oauth/<google|microsoft>/callback` en el
   portal de OAuth y **reautoriza** los tenants.
3. **Comprobar salud**: `curl https://api.tudominio.com/health` → `{"status":"healthy"}`.
4. **Base de datos**: si se conserva el historial, respalda el SQL actual y
   restáuralo en el servidor; si se parte de cero, el sistema crea las tablas
   automáticamente con las migraciones al arrancar.

## 6. Comandos útiles (en el servidor, `cd /opt/agenda-api/app`)

```bash
docker compose --env-file ../.env up -d --build   # levantar/actualizar
docker compose down                               # apagar
docker logs agenda-api -f                         # logs de la API en vivo
docker ps                                         # estado de los contenedores
```

El compose ya usa `restart: always`, así que los contenedores **se reinician
solos** ante caídas y al reiniciar el servidor.

---

**Resumen de la cuenta del dueño que debes pedir (por canal seguro):**
`GROQ_API_KEY`, `WHATSAPP_ACCESS_TOKEN`, `WHATSAPP_PHONE_NUMBER_ID`,
`WHATSAPP_VERIFY_TOKEN`, `GoogleOAuth__*`, `MicrosoftOAuth__*` y el dominio
público que se usará.