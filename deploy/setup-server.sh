#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════
#   AgendaApi - Despliegue en servidor en la nube (VPS Linux)
#   Ubuntu 24.04 LTS
#
#   Uso:
#     sudo ./setup-server.sh api.tudominio.com
#
#   Hace TODO de una vez:
#     1. Instala Docker + Docker Compose plugin
#     2. Genera claves secretas seguras (JWT, MasterKey, SQL)
#     3. Crea /opt/agenda-api con el .env (edítalo con tus claves)
#     4. Clona el repo y compila la imagen (Dockerfile multi-etapa)
#     5. Levanta SQL Server + API en contenedores
#     6. Instala Nginx + Let's Encrypt (HTTPS) para tu dominio
#
#   Después de correr: edita /opt/agenda-api/.env y reinicia la API.
# ═══════════════════════════════════════════════════════════════
set -euo pipefail

DOMAIN="${1:-}"
if [ -z "$DOMAIN" ]; then
    echo "Uso: $0 api.tudominio.com"
    exit 1
fi

echo "🚀 Desplegando AgendaApi en la nube para: $DOMAIN"

# ─── 1. Sistema base ─────────────────────────────────────────
echo "[1/9] Actualizando sistema..."
apt update && apt upgrade -y
apt install -y curl wget git ufw ca-certificates gnupg tree

# ─── 2. Docker ───────────────────────────────────────────────
echo "[2/9] Instalando Docker..."
curl -fsSL https://get.docker.com | bash
systemctl enable --now docker
apt install -y docker-compose-plugin
docker compose version

# ─── 3. Firewall (solo 80/443; la API va detrás de nginx) ──
echo "[3/9] Configurando firewall..."
ufw allow OpenSSH
ufw allow 80/tcp
ufw allow 443/tcp
ufw --force enable

# ─── 4. Estructura ───────────────────────────────────────────
echo "[4/9] Creando estructura en /opt/agenda-api..."
mkdir -p /opt/agenda-api/app
cd /opt/agenda-api

# ─── 5. Claves secretas ──────────────────────────────────────
echo "[5/9] Generando claves seguras..."
MASTER_KEY=$(openssl rand -base64 32)
JWT_SECRET=$(openssl rand -base64 32)          # >=32 chars, válido para JWT
SQL_PASSWORD=$(openssl rand -base64 16 | tr '+/' '_-')

# ─── 6. .env (actualizado al esquema del docker-compose) ─────
echo "[6/9] Creando .env (EDÍTALO con tus claves reales)..."
cat > .env << EOF
# ═══════════════════════════════════════════════════════════
#   AgendaApi - Variables de entorno (PRODUCCIÓN)
# ═══════════════════════════════════════════════════════════

# --- Base de datos (docker-compose arma la cadena con SQL_PASSWORD) ---
SQL_PASSWORD=${SQL_PASSWORD}

# --- Seguridad ---
JWT_SECRET=${JWT_SECRET}
MASTER_KEY=${MASTER_KEY}

# --- IA (cadena de respaldo: Groq -> OpenAI -> Anthropic -> groq) ---
GROQ_API_KEY=                # ← PONER TU CLAVE  (provider principal)
OpenRouter__ApiKey=           # opcional

# --- WhatsApp ---
WHATSAPP_ACCESS_TOKEN=       # ← PONER TU TOKEN de WhatsApp Cloud API
WHATSAPP_PHONE_NUMBER_ID=    # ← PONER TU ID de número
WHATSAPP_VERIFY_TOKEN=agenda_api_prod_2024
NOTIFICACIONES_WHATSAPP_DUENO=   # número del dueño para avisos/handoff
WHATSAPP_RECORDATORIO_TEMPLATE_24H=   # template aprobado (opcional)
WHATSAPP_RECORDATORIO_TEMPLATE_2H=    # template aprobado (opcional)

# --- Google Calendar (opcional; redirect en tu dominio) ---
GoogleOAuth__ClientId=
GoogleOAuth__ClientSecret=

# --- Microsoft 365 / Outlook (opcional) ---
MicrosoftOAuth__ClientId=
MicrosoftOAuth__ClientSecret=

# --- Dominio público / zona horaria ---
PUBLIC_BASE_URL=https://${DOMAIN}
Calendar__TimeZone=America/Bogota
EOF
chmod 600 .env
echo "   ✅ .env creado. EDÍTALO: nano /opt/agenda-api/.env"

# ─── 7. Código + build de imagen ─────────────────────────────
echo "[7/9] Clonando repositorio y compilando imagen (multi-etapa)..."
# El Dockerfile multi-etapa compila desde el CÓDIGO, así que copiamos
# todo el repo (proyectos, .sln, Dockerfile, compose, scripts).
if [ -d /tmp/agenda-repo ]; then rm -rf /tmp/agenda-repo; fi
if [ -n "$(ls -A /opt/agenda-api/app 2>/dev/null)" ]; then rm -rf /opt/agenda-api/app; mkdir -p /opt/agenda-api/app; fi
git clone https://github.com/rioskevin277-dev/Agenda-Citas.git /opt/agenda-api/app
# Copiar el .env del host al directorio del compose (docker compose lo lee)
mkdir -p /opt/agenda-api/logs

# Compila la imagen (SQL + API se levantan juntos)
cd /opt/agenda-api/app
set +e
docker compose --env-file ../.env up -d --build
BUILD_RC=$?
set -e
if [ $BUILD_RC -ne 0 ]; then
    echo "⚠️  docker compose build falló. Revisa arriba. Puede ser:"
    echo "   - Falta espacio (df -h) / RAM insuficiente para SQL Server (mín 2 GB)."
    echo "   - Corre de nuevo tras liberar recursos: cd /opt/agenda-api/app && docker compose --env-file ../.env up -d --build"
    exit 1
fi
echo "   ✅ Contenedores levantados: $(docker ps --format '{{.Names}}')"

# ─── 8. Nginx + HTTPS ────────────────────────────────────────
echo "[8/9] Instalando Nginx + Let's Encrypt..."
apt install -y nginx certbot python3-certbot-nginx
cat > /etc/nginx/sites-available/agenda-api << EOF
server {
    listen 80;
    server_name ${DOMAIN};
    client_max_body_size 10M;
    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 90s;
        proxy_send_timeout 90s;
    }
}
EOF
ln -sf /etc/nginx/sites-available/agenda-api /etc/nginx/sites-enabled/
rm -f /etc/nginx/sites-enabled/default
nginx -t && systemctl reload nginx

certbot --nginx -d ${DOMAIN} --non-interactive --agree-tos --redirect \
    -m admin@${DOMAIN} 2>/dev/null || echo "⚠️  Certbot/SSL: ejecuta manualmente: certbot --nginx -d ${DOMAIN}"

# ─── 9. Resumen ──────────────────────────────────────────────
echo "[9/9] Verificando salud..."
sleep 8
echo "   Local health:"
curl -s http://localhost:8080/health && echo
echo ""
echo "════════════════════════════════════════════════"
echo "   ✅ SETUP EN LA NUBE COMPLETADO"
echo "════════════════════════════════════════════════"
echo ""
echo "   Web:      https://${DOMAIN}"
echo "   Health:   https://${DOMAIN}/health"
echo "   Swagger:  https://${DOMAIN}/swagger"
echo ""
echo "   ‼ Próximos PASOS MANUALES obligatorios:"
echo "     1) nano /opt/agenda-api/.env   → rellena tus claves"
echo "     2) docker compose restart api   (cd /opt/agenda-api/app)"
echo "     3) Apunta el webhook de WhatsApp a https://${DOMAIN}/api/v1/webhook"
echo "     4) Registra/traduce los OAuth (Google/M365) al nuevo dominio"
echo ""
echo "   🔑 SQL_PASSWORD (guárdala en un lugar seguro):"
echo "      ${SQL_PASSWORD}"
echo "   Logs API:  docker logs agenda-api -f"
echo "   Detener:   cd /opt/agenda-api/app && docker compose down"
echo ""