#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Despliegue COMPLETO de AgendaApi a producción (un solo comando).
.DESCRIPTION
    Automatiza TODO el ciclo de producción para que no tengas que hacer nada a mano:

      1. Verifica que .env exista y tenga las claves críticas.
      2. Publica el binario (dotnet publish -> publish_local).  [el paso que suele olvidarse]
      3. Asegura que Docker Desktop esté corriendo.
      4. Reconstruye y levanta los contenedores (docker compose up -d --build).
      5. Espera a que la API responda en local.
      6. Asegura que el túnel de Cloudflare esté activo.
      7. Verifica el health REAL del dominio público y reporta el estado.

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
Write-Step "1/7 Verificando configuración (.env)..."
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

# docker-compose.yml lee estas en ALL_CAPS. Validar presencia (no valor).
$required = @(
    'SQL_PASSWORD',
    'JWT_SECRET',
    'MASTER_KEY',
    'GROQ_API_KEY',
    'WHATSAPP_ACCESS_TOKEN',
    'WHATSAPP_PHONE_NUMBER_ID',
    'WHATSAPP_VERIFY_TOKEN',
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
foreach ($k in @('GROQ_API_KEY','JWT_SECRET','MASTER_KEY','WHATSAPP_ACCESS_TOKEN')) {
    $val = [System.Environment]::GetEnvironmentVariable($k)
    foreach ($p in $placeholders) {
        if ($val -like "*$p*") { $suspects += $k; break }
    }
}
if ($suspects) {
    Write-Warn "Posibles placeholders (revisa estas claves): $($suspects -join ', ')"
}
Write-Ok "Configuración presente"

# ─── 2. Publicar binario ──────────────────────────────────────────
Write-Step "2/7 Publicando binario (dotnet publish -> publish_local)..."
dotnet publish "$ProjectRoot\AgendaApi.Api\AgendaApi.Api.csproj" -c Release -o "$ProjectRoot\publish_local"
if ($LASTEXITCODE -ne 0) {
    Write-Err "Falló dotnet publish"
    exit 1
}
Write-Ok "Binario publicado (el contenedor copiará el código NUEVO)"

# ─── 3. Asegurar Docker Desktop ───────────────────────────────────
Write-Step "3/7 Asegurando Docker Desktop..."
$dockerProc = Get-Process "Docker Desktop" -ErrorAction SilentlyContinue
if (-not $dockerProc) {
    $dockerExe = "$env:ProgramFiles\Docker\Docker\Docker Desktop.exe"
    if (-not (Test-Path $dockerExe)) {
        $dockerExe = "$env:LOCALAPPDATA\Docker\Docker Desktop\Docker Desktop.exe"  # carpeta AppData (tu caso)
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
Write-Step "4/7 Reconstruyendo y levantando contenedores (docker compose up -d --build)..."
wsl -d $WslDistro bash -c "cd /mnt/c/Users/USUARIO/agenda-api && docker compose up -d --build api 2>&1"
if ($LASTEXITCODE -ne 0) {
    Write-Err "Falló docker compose up --build"
    exit 1
}
Write-Ok "Contenedores levantados"

# ─── 5. Esperar health local ──────────────────────────────────────
Write-Step "5/7 Esperando a que la API responda en local..."
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

# ─── 6. Asegurar Cloudflare Tunnel ────────────────────────────────
Write-Step "6/7 Asegurando Cloudflare Tunnel..."
$tunnelRunning = wsl -d $WslDistro bash -c "ps aux | grep cloudflared | grep -v grep" 2>&1
if (-not $tunnelRunning) {
    Write-Warn "El túnel no estaba corriendo, iniciándolo..."
    wsl -d $WslDistro -u $WslUser bash -c "nohup cloudflared tunnel run $TunnelName > /dev/null 2>&1 &"
    Start-Sleep -Seconds 8
} else {
    Write-Ok "El túnel de Cloudflare ya está corriendo"
}

# ─── 7. Verificar dominio público ─────────────────────────────────
Write-Step "7/7 Verificando el dominio público ($PublicUrl/health)..."
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