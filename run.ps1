#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Inicia AgendaApi localmente cargando .env como variables de entorno.
.DESCRIPTION
    Lee .env de la raiz del proyecto, establece cada variable como
    environment variable y ejecuta dotnet run. Sin Docker, sin tunel,
    solo localhost.

    Requisitos:
    - .NET 8 SDK
    - SQL Server accesible (Docker o local)
    - OpenAI API Key en .env

    Uso:
        .\run.ps1
#>

$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ProjectRoot

# --- Verificar .NET SDK ------------------------------------------------
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host "[X] .NET SDK no encontrado. Instalar desde: https://dotnet.microsoft.com/download" -ForegroundColor Red
    exit 1
}

# --- Verificar .env -----------------------------------------------------
$envFile = Join-Path $ProjectRoot ".env"
if (-not (Test-Path $envFile)) {
    Write-Host "[X] No se encuentra .env en $ProjectRoot" -ForegroundColor Red
    Write-Host "   Copiar desde: copy .env.example .env" -ForegroundColor Yellow
    exit 1
}

# --- Cargar .env --------------------------------------------------------
Write-Host "[i] Cargando .env..." -ForegroundColor Cyan
$count = 0
Get-Content $envFile | ForEach-Object {
    # Saltar comentarios y lineas vacias
    if ($_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') {
        $key = $matches[1]
        $value = $matches[2].Trim()
        # Quitar comillas dobles si existen
        if ($value -match '^"(.*)"$') {
            $value = $matches[1]
        }
        # Quitar comillas simples si existen
        if ($value -match "^'(.*)'$") {
            $value = $matches[1]
        }
        Set-Item -Path "env:$key" -Value $value
        $count++
    }
}
Write-Host "   [OK] $count variables cargadas" -ForegroundColor Green

# --- Resumen de configuración ------------------------------------------
Write-Host ""
Write-Host "[i] Configuracion:" -ForegroundColor Cyan
Write-Host "   Connection String:    $($env:ConnectionStrings__AgendaDb)" -ForegroundColor Gray
Write-Host "   OpenAI:               $(if ($env:OpenAI__ApiKey -and $env:OpenAI__ApiKey -ne 'sk-xxx') { '[OK] configurada' } else { '[!] sk-xxx (placeholder)' })" -ForegroundColor $(if ($env:OpenAI__ApiKey -and $env:OpenAI__ApiKey -ne 'sk-xxx') { 'Green' } else { 'Yellow' })
Write-Host "   JWT Secret:           $(if ($env:Jwt__Secret) { '[OK] configurado' } else { '[X] faltante' })" -ForegroundColor $(if ($env:Jwt__Secret) { 'Green' } else { 'Red' })
Write-Host "   Master Key:           $(if ($env:TokenEncryption__MasterKey) { '[OK] configurada' } else { '[X] faltante' })" -ForegroundColor $(if ($env:TokenEncryption__MasterKey) { 'Green' } else { 'Red' })
Write-Host "   Log path:             $($env:LOG_PATH)" -ForegroundColor Gray
Write-Host ""

# --- Validación mínima --------------------------------------------------
$errors = @()
if (-not $env:OpenAI__ApiKey -or $env:OpenAI__ApiKey -eq 'sk-xxx') {
    $errors += "OPENAI_API_KEY / OpenAI__ApiKey - editar .env con una API key real"
}
if (-not $env:Jwt__Secret) {
    $errors += "JWT_SECRET / Jwt__Secret - debe tener al menos 32 caracteres"
}
if (-not $env:TokenEncryption__MasterKey) {
    $errors += "MASTER_KEY / TokenEncryption__MasterKey - generar con: openssl rand -base64 32"
}

if ($errors.Count -gt 0) {
    Write-Host "[!] Advertencias de configuracion:" -ForegroundColor Yellow
    $errors | ForEach-Object { Write-Host "   - $_" -ForegroundColor Yellow }
    Write-Host ""
}

# --- Restaurar paquetes ------------------------------------------------
Write-Host "[i] Restaurando paquetes..." -ForegroundColor Cyan
dotnet restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "[X] Error restaurando paquetes" -ForegroundColor Red
    exit 1
}

# --- Migraciones --------------------------------------------------------
Write-Host "[i] Aplicando migraciones..." -ForegroundColor Cyan
dotnet ef database update --project AgendaApi.Infrastructure --startup-project AgendaApi.Api
if ($LASTEXITCODE -ne 0) {
    Write-Host "[!] Error en migraciones. SQL Server esta corriendo?" -ForegroundColor Yellow
    Write-Host "   Si usas Docker: docker compose up -d sqlserver" -ForegroundColor Yellow
}

# --- Iniciar API ---------------------------------------------------------
Write-Host ""
Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "   Iniciando AgendaApi..." -ForegroundColor Cyan
Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host ""
$port = if ($env:ASPNETCORE_URLS -match '.*:(\d+)') { $matches[1] } else { "5000" }
Write-Host "   Swagger:  http://localhost:$port/swagger" -ForegroundColor White
Write-Host "   Health:   http://localhost:$port/health" -ForegroundColor White
Write-Host "   Dashboard en vivo: http://localhost:$port/api/v1/dashboard/conversations/page" -ForegroundColor White
Write-Host "   APIs:     http://localhost:$port/api/*" -ForegroundColor White
Write-Host ""
Write-Host "   Para detener: Ctrl+C" -ForegroundColor Gray
Write-Host ""

dotnet run --project AgendaApi.Api