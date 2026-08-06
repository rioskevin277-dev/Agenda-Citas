#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Inicia AgendaApi: Docker + Cloudflare Tunnel
.DESCRIPTION
    Ejecutar desde PowerShell (como admin la primera vez).
    Asegura que Docker Desktop este corriendo, levanta los contenedores
    y conecta el Cloudflare Tunnel.
#>

Write-Host "AG AgendaApi - Inicio de Produccion" -ForegroundColor Cyan

# 1. Iniciar Docker Desktop si no esta corriendo
$dockerProcess = Get-Process "Docker Desktop" -ErrorAction SilentlyContinue
if (-not $dockerProcess) {
    Write-Host "[..] Iniciando Docker Desktop..." -ForegroundColor Yellow
    Start-Process "$env:ProgramFiles\Docker\Docker\Docker Desktop.exe"
    Write-Host "   Esperando a que Docker este listo..." -ForegroundColor Yellow
    Start-Sleep -Seconds 15
} else {
    Write-Host "[OK] Docker Desktop ya esta corriendo" -ForegroundColor Green
}

# 2. Esperar a que Docker responda
$maxRetries = 30
$retry = 0
$dockerOk = $false
do {
    $retry++
    $null = & docker ps 2>&1
    if ($LASTEXITCODE -eq 0) { $dockerOk = $true; break }
    Write-Host "   [..] Esperando Docker... ($retry/$maxRetries)" -ForegroundColor Yellow
    Start-Sleep -Seconds 2
} while ($retry -lt $maxRetries)

if (-not $dockerOk) {
    Write-Host "[XX] Docker no responde. Abre Docker Desktop manualmente." -ForegroundColor Red
    exit 1
}
Write-Host "[OK] Docker listo" -ForegroundColor Green

# 3. Ir al proyecto
$projectDir = "C:\Users\USUARIO\agenda-api"
Set-Location $projectDir

# 4. Levantar servicios Docker
Write-Host "[..] Levantando servicios..." -ForegroundColor Yellow
wsl -d Ubuntu-24.04 bash -c "cd /mnt/c/Users/USUARIO/agenda-api && docker compose up -d 2>&1"
Write-Host "[OK] Contenedores iniciados" -ForegroundColor Green

# 5. Iniciar Cloudflare Tunnel
Write-Host "[..] Conectando Cloudflare Tunnel..." -ForegroundColor Yellow
$tunnelRunning = wsl -d Ubuntu-24.04 bash -c "ps aux | grep cloudflared | grep -v grep" 2>&1
if (-not $tunnelRunning) {
    wsl -d Ubuntu-24.04 -u usuario bash -c "nohup cloudflared tunnel run agenda-api > /dev/null 2>&1 &"
    Write-Host "[OK] Tunnel iniciado" -ForegroundColor Green
} else {
    Write-Host "[OK] Tunnel ya esta corriendo" -ForegroundColor Green
}

Write-Host ""
Write-Host "===============================" -ForegroundColor Cyan
Write-Host "   AgendaApi - PRODUCCION ACTIVA" -ForegroundColor Cyan
Write-Host "===============================" -ForegroundColor Cyan
Write-Host ""
Write-Host "   Web:      https://api.adamcoia.com" -ForegroundColor White
Write-Host "   Swagger:  https://api.adamcoia.com/swagger" -ForegroundColor White
Write-Host "   Health:   https://api.adamcoia.com/health" -ForegroundColor White
Write-Host "   Local:    http://localhost:8080" -ForegroundColor White
Write-Host ""
Write-Host "   Para apagar: docker compose down" -ForegroundColor Gray
Write-Host "   Para logs:   docker logs agenda-api -f" -ForegroundColor Gray
Write-Host ""