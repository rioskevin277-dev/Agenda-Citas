#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Despliegue COMPLETO de AgendaApi a producción (un solo comando).
.DESCRIPTION
    Automatiza TODO el ciclo de producción para que no tengas que hacer nada a mano:

      1. Verifica que .env exista y tenga las claves críticas.
      2. Asegura que el engine de Docker esté corriendo.
      3. Reconstruye y levanta los contenedores (docker compose up -d --build).
      4. Espera a que la API responda en local.
      5. Asegura que el túnel de Cloudflare esté activo.
      6. Verifica el health REAL del dominio público y reporta el estado.

    Uso:
        .\scripts\deploy-production.ps1

    O simplemente:
        .\deploy.ps1      (acceso rápido, ver nota al final)

    Requisitos:
        - .NET 8 SDK
        - Docker Desktop (AppData o Program Files)
        - WSL distro Ubuntu-24.04 con docker compose y cloudflared instalados
        - Un .env en la raíz del proyecto con las claves críticas
#>

$ErrorActionPreference = "Stop"
$ProjectRoot   = "C:\Users\USUARIO\agenda-api"
$WslDistro     = "Ubuntu-24.04"
$WslUser       = "usuario"
$TunnelName    = "agenda-api"
$PublicUrl     = "https://api.adamcoia.com"
$envFile       = Join-Path $ProjectRoot ".env"

Set-Location $ProjectRoot

function Write-Step($msg)  { Write-Host "`n[$([DateTime]::Now.ToString('HH:mm:ss'))] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)    { Write-Host "   [OK] $msg" -ForegroundColor Green }
function Write-Warn($msg)  { Write-Host "   [!!] $msg" -ForegroundColor Yellow }
function Write-Err($msg)   { Write-Host "   [XX] $msg" -ForegroundColor Red }

Write-Host ""
Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "   🚀 AgendaApi - DESPLIEGUE DE PRODUCCIÓN" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan

# ─── 1. Validar .env y claves críticas ─────────────────────────────
Write-Step "1/6 Verificando configuración (.env)..."
if (-not (Test-Path $envFile)) {
    Write-Err "No se encuentra .env en $ProjectRoot"
    Write-Err "Copia desde: copy .env.example .env"
    exit 1
}

# Cargar .env como variables para poder validar localmente
Get-Content $envFile | ForEach-Object {
    if ($_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') {
        $k = $matches[1]; $v = $matches[2].Trim()
        if ($v -match '^"(.*)"$') { $v = $matches[1] }
        if ($v -match "^'(.*)'$")  { $v = $matches[1] }
        Set-Item -Path "env:$k" -Value $v
    }
}

# docker-compose.yml lee estos nombres (schema ASP.NET Section__Key). Validar presencia (no valor).
$required = @(
    'SQL_PASSWORD',
    'TokenEncryption__MasterKey',
    'DASHBOARD_KEY',
    'Jwt__Secret',
    'OpenAI__ApiKey',
    'WhatsApp__AccessToken',
    'WhatsApp__PhoneNumberId',
    'WhatsApp__VerifyToken',
    'Groq__ApiKey',
    'OpenRouter__ApiKey',
    'PUBLIC_BASE_URL'
)
$missing = $required | Where-Object { -not [System.Environment]::GetEnvironmentVariable($_) }
if ($missing) {
    Write-Err "Faltan claves en .env:"
    $missing | ForEach-Object { Write-Err "   - $_" }
    exit 1
}
$placeholders = @('sk-xxx','ChangeMe')
$suspects = @()
foreach ($k in @('OpenAI__ApiKey','Groq__ApiKey','Jwt__Secret','TokenEncryption__MasterKey','WhatsApp__AccessToken','OpenRouter__ApiKey')) {
    $val = [System.Environment]::GetEnvironmentVariable($k)
    foreach ($p in $placeholders) {
        if ($val -like "*$p*") { $suspects += $k; break }
    }
}
if ($suspects) {
    Write-Warn "Posibles placeholders (revisa estas claves): $($suspects -join ', ')"
}
Write-Ok "Configuración presente"

# ─── 2. Asegurar el engine de Docker ──────────────────────────────
# (el Dockerfile compila desde el código dentro del contenedor; no hace falta publicar localmente)
Write-Step "2/6 Asegurando el engine de Docker..."
$dockerProc = Get-Process "Docker Desktop" -ErrorAction SilentlyContinue
if (-not $dockerProc) {
    $dockerExe = "$env:ProgramFiles\Docker\Docker\Docker Desktop.exe"
    if (-not (Test-Path $dockerExe)) {
        $dockerExe = "$env:LOCALAPPDATA\Docker\Docker Desktop\Docker Desktop.exe"  # carpeta AppData (tu caso)
        if (-not (Test-Path $dockerExe)) { $dockerExe = "$env:LOCALAPPDATA\Programs\DockerDesktop\Docker Desktop.exe" }
    }
    if (Test-Path $dockerExe) {
        Write-Warn "Docker Desktop no estaba corriendo, iniciándolo..."
        Start-Process $dockerExe
    } else {
        Write-Err "No encuentro Docker Desktop. Ábrelo manualmente."
        exit 1
    }
} else {
    Write-Ok "Docker Desktop ya está corriendo"
}

$maxRetries = 45; $retry = 0; $dockerOk = $false
do {
    $retry++
    $null = & docker ps 2>&1
    if ($LASTEXITCODE -eq 0) { $dockerOk = $true; break }
    Start-Sleep -Seconds 2
} while ($retry -lt $maxRetries)
if (-not $dockerOk) {
    Write-Err "Docker no responde tras $maxRetries intentos. Ábrelo manualmente y vuelve a correr el script."
    exit 1
}
Write-Ok "Docker listo"

# ─── 4. Reconstruir y levantar contenedores ───────────────────────
Write-Step "3/6 Reconstruyendo y levantando contenedores (docker compose up -d --build)..."
wsl -d $WslDistro bash -c "cd /mnt/c/Users/USUARIO/agenda-api && docker compose up -d --build 2>&1"
if ($LASTEXITCODE -ne 0) {
    Write-Err "Falló docker compose up --build"
    exit 1
}
Write-Ok "Contenedores levantados"

# ─── 5. Esperar health local ──────────────────────────────────────
Write-Step "4/6 Esperando a que la API responda en local..."
$healthOk = $false
for ($i = 1; $i -le 30; $i++) {
    $resp = try { Invoke-WebRequest -Uri "http://localhost:8080/health" -UseBasicParsing -TimeoutSec 3 } catch { $null }
    if ($resp -and $resp.StatusCode -eq 200) {
        Write-Ok "API local responde: $($resp.Content)"
        $healthOk = $true
        break
    }
    Start-Sleep -Seconds 2
}
if (-not $healthOk) {
    Write-Warn "La API local no respondió en 60s. Revisa con: docker logs agenda-api -f"
}

# ─── 6. Asegurar Cloudflare Tunnel (ahora como contenedor) ────────
Write-Step "5/6 Asegurando Cloudflare Tunnel (contenedor)..."
# Detiene cualquier túnel WSL viejo (ya no se usa; el túnel es el contenedor)
wsl -d $WslDistro bash -c "pkill -f cloudflared 2>/dev/null; true" 2>$null
# Levanta el contenedor cloudflared y REINTENTA si crashea en el arranque en frío
# del engine de Docker (exited 127) — ese es el caso que dejaba el túnel caído.
$cfRunning = $false
for ($i = 1; $i -le 5; $i++) {
    $cfUp = "cd /mnt/c/Users/USUARIO/agenda-api && docker compose up -d cloudflared 2>&1"
    wsl -d $WslDistro bash -c $cfUp | Out-Host
    # Espera a que registre al menos una conexión de túnel (o salga de nuevo)
    Start-Sleep -Seconds 8
    $st = & docker inspect -f '{{.State.Status}}' agenda-cloudflared 2>$null
    if ($st -eq "running") {
        Write-Ok "Túnel Cloudflare (contenedor) activo (intento $i)"
        $cfRunning = $true
        break
    }
    Write-Warn "cloudflared no quedó arriba (estado: '$st'), reintentando... ($i/5)"
    & docker stop agenda-cloudflared 2>$null | Out-Null
    Start-Sleep -Seconds 5
}
if (-not $cfRunning) {
    Write-Warn "El contenedor cloudflared no arrancó tras 5 intentos. Revisa: docker logs agenda-cloudflared"
}

# ─── 7. Verificar dominio público ─────────────────────────────────
Write-Step "6/6 Verificando el dominio público ($PublicUrl/health)..."
$pubOk = $false
for ($i = 1; $i -le 10; $i++) {
    $resp = try { Invoke-WebRequest -Uri "$PublicUrl/health" -UseBasicParsing -TimeoutSec 5 } catch { $null }
    if ($resp -and $resp.StatusCode -eq 200) {
        Write-Ok "Producción responde correctamente: $($resp.Content)"
        $pubOk = $true
        break
    }
    Start-Sleep -Seconds 3
}

Write-Host ""
Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan
if ($pubOk) {
    Write-Host "   ✅ PRODUCCIÓN ACTIVA Y VERIFICADA" -ForegroundColor Green
} else {
    Write-Host "   ⚠️  Contenedores levantados, pero el dominio no responde aún." -ForegroundColor Yellow
    Write-Host "      Espera 20-30s (túnel + DNS) y revisa: docker logs agenda-api -f" -ForegroundColor Yellow
}
Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Write-Host "   Web:      $PublicUrl"                        -ForegroundColor White
Write-Host "   Swagger:  $PublicUrl/swagger"                -ForegroundColor White
Write-Host "   Local:    http://localhost:8080"             -ForegroundColor White
Write-Host ""
Write-Host "   Logs en vivo:    docker logs agenda-api -f"   -ForegroundColor Gray
Write-Host "   Apagar todo:     docker compose down"         -ForegroundColor Gray
Write-Host ""
Write-Host "Para un acceso rápido, usa el deploy.ps1 de la raíz."
Write-Host '   & "C:\Users\USUARIO\agenda-api\scripts\deploy-production.ps1"' -ForegroundColor Gray
Write-Host ""