#!/usr/bin/env bash
set -euo pipefail

# ═══════════════════════════════════════════
#   AgendaApi - Setup de Producción
#   Ubuntu 22.04 / 24.04 LTS
# ═══════════════════════════════════════════

DOMAIN="${1:-}"  # ej: api.tudominio.com
if [ -z "$DOMAIN" ]; then
    echo "Uso: $0 api.tudominio.com"
    echo "Ej:  $0 api.miagenda.com"
    exit 1
fi

echo "🚀 Instalando AgendaApi en producción para: $DOMAIN"

# ─── 1. Actualizar sistema ─────────────────────
echo "📦 Actualizando sistema..."
apt update && apt upgrade -y
apt install -y curl wget git ufw

# ─── 2. Instalar Docker ─────────────────────────
echo "🐳 Instalando Docker..."
curl -fsSL https://get.docker.com | bash
systemctl enable --now docker

# ─── 3. Instalar Docker Compose plugin ──────────
echo "🐳 Instalando Docker Compose..."
apt install -y docker-compose-plugin
docker compose version

# ─── 4. Firewall ─────────────────────────────
echo "🔒 Configurando firewall..."
ufw allow OpenSSH
ufw allow 80/tcp
ufw allow 443/tcp
ufw --force enable

# ─── 5. Crear estructura ────────────────────
mkdir -p /opt/agenda-api
cd /opt/agenda-api

# ─── 6. Generar secrets ─────────────────────
echo "🔑 Generando claves..."
MASTER_KEY=$(openssl rand -base64 32)
JWT_SECRET=$(openssl rand -base64 32)
SQL_PASSWORD=$(openssl rand -base64 16 | tr '+/' '_-')

# ─── 7. Crear .env ──────────────────────────
cat > .env << EOF
# ═══════════════════════════════════════════
#   AgendaApi - Variables de Entorno
# ═══════════════════════════════════════════

# --- SQL Server ---
SQL_PASSWORD=${SQL_PASSWORD}

# --- JWT ---
JWT_SECRET=${JWT_SECRET}

# --- Token Encryption (AES-256) ---
MASTER_KEY=${MASTER_KEY}

# --- AI Providers ---
OPENAI_API_KEY=sk-xxx          # ← CAMBIAR
ANTHROPIC_API_KEY=              # opcional

# --- WhatsApp ---
WHATSAPP_ACCESS_TOKEN=          # ← CAMBIAR: token de acceso WhatsApp Cloud API
WHATSAPP_PHONE_NUMBER_ID=       # ← CAMBIAR: ID del número de teléfono
WHATSAPP_VERIFY_TOKEN=agenda_api_prod_2024

# --- Google OAuth (para Google Calendar) ---
GOOGLE_OAUTH_CLIENT_ID=         # ← CAMBIAR (opcional)
GOOGLE_OAUTH_CLIENT_SECRET=     # ← CAMBIAR (opcional)

# --- Microsoft OAuth (para Microsoft 365) ---
MS_OAUTH_CLIENT_ID=             # ← CAMBIAR (opcional)
MS_OAUTH_CLIENT_SECRET=         # ← CAMBIAR (opcional)
EOF

chmod 600 .env
echo "✅ .env creado en /opt/agenda-api/.env"
echo "⚠️  EDITALO: nano /opt/agenda-api/.env"
echo "   y completa las claves de OpenAI, WhatsApp, etc."

# ─── 8. Clonar código ──────────────────────
echo "📥 Clonando repositorio..."
git clone https://github.com/TU_USUARIO/agenda-api.git /tmp/agenda-repo
cp /tmp/agenda-repo/Dockerfile /opt/agenda-api/
cp /tmp/agenda-repo/docker-compose.yml /opt/agenda-api/
rm -rf /tmp/agenda-repo

# ─── 9. Crear estructura de logs ───────────
mkdir -p /opt/agenda-api/logs

# ─── 10. Iniciar servicios ─────────────────
echo "🚀 Iniciando servicios..."
docker compose up -d
echo "✅ Servicios iniciados"

# ─── 11. Nginx con Let's Encrypt ───────────
echo "🌐 Instalando Nginx + Certbot..."
apt install -y nginx certbot python3-certbot-nginx

cat > /etc/nginx/sites-available/agenda-api << EOF
server {
    listen 80;
    server_name ${DOMAIN};
    return 301 https://\$server_name\$request_uri;
}

server {
    listen 443 ssl http2;
    server_name ${DOMAIN};

    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 90s;
        proxy_send_timeout 90s;
    }

    # Webhook de WhatsApp y calendarios (body grande)
    location /api/webhook {
        client_max_body_size 10M;
        proxy_pass http://127.0.0.1:5000;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 30s;
    }
}
EOF

ln -sf /etc/nginx/sites-available/agenda-api /etc/nginx/sites-enabled/
rm -f /etc/nginx/sites-enabled/default

# Obtener SSL
certbot --nginx -d ${DOMAIN} --non-interactive --agree-tos --email admin@${DOMAIN} || {
    echo "⚠️  Certbot falló. Ejecuta manualmente:"
    echo "   certbot --nginx -d ${DOMAIN}"
}

nginx -t && systemctl reload nginx

echo ""
echo "═══════════════════════════════════════════"
echo "   ✅ SETUP COMPLETADO"
echo "═══════════════════════════════════════════"
echo ""
echo "   Web:      https://${DOMAIN}"
echo "   Health:   https://${DOMAIN}/health"
echo "   Swagger:  https://${DOMAIN}/swagger"
echo ""
echo "   📝 Edita las variables:"
echo "      nano /opt/agenda-api/.env"
echo "   Luego reinicia:"
echo "      docker compose restart api"
echo ""
echo "   🔑 SQL Password: ${SQL_PASSWORD}"
echo "      (guardado en /opt/agenda-api/.env)"
echo ""
