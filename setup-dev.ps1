#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Setup script para desarrollo local de AgendaApi.
.DESCRIPTION
    Configura variables de entorno, aplica migraciones, corre tests e inicia el proyecto.
    Ejecutar desde la raíz del repositorio.
#>

param(
    [switch]$SkipTests,
    [switch]$SkipMigrations
)

Write-Host "🚀 AgendaApi - Setup de Desarrollo" -ForegroundColor Cyan
Write-Host "====================================`n" -ForegroundColor Cyan

# ─── Variables de Entorno ─────────────────────────────────────

# SQL Server — usa Trusted_Connection (Windows Auth) por defecto como appsettings.json
# Cambiar a User Id=sa;Password=... si se requiere autenticación SQL
$useSqlAuth = Read-Host "¿Usar autenticación SQL Server? (s/N)"
if ($useSqlAuth -eq "s" -or $useSqlAuth -eq "S") {
    $dbPassword = Read-Host "SQL Server SA password"
    $env:ConnectionStrings__AgendaDb = "Server=localhost;Database=AgendaDb;User Id=sa;Password=$dbPassword;TrustServerCertificate=True;MultipleActiveResultSets=true"
} else {
    $env:ConnectionStrings__AgendaDb = "Server=localhost;Database=AgendaDb;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
    Write-Host "  ✅ Usando Windows Auth (Trusted_Connection)" -ForegroundColor Green
}

# JWT
$env:Jwt__Secret = "DevSecret_AgendaApi_2024_Min32Chars!!!"

# OpenAI
$openAiKey = Read-Host "OpenAI API Key (sk-...)"
if ($openAiKey) { $env:OpenAI__ApiKey = $openAiKey }

# Anthropic
$anthropicKey = Read-Host "Anthropic API Key (opcional, Enter para omitir)"
if ($anthropicKey) { $env:Anthropic__ApiKey = $anthropicKey }

# WhatsApp
$waToken = Read-Host "WhatsApp Access Token (opcional, Enter para omitir)"
if ($waToken) {
    $env:WhatsApp__AccessToken = $waToken
    $env:WhatsApp__PhoneNumberId = Read-Host "WhatsApp Phone Number ID"
    $env:WhatsApp__VerifyToken = "agenda_api_dev_token"
} else {
    $env:WhatsApp__VerifyToken = "agenda_api_dev_token"
    Write-Host "  ⚠️  WhatsApp sin configurar (solo webhook de prueba)" -ForegroundColor Yellow
}

# Token Encryption (AES-256)
$masterKey = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:TokenEncryption__MasterKey = $masterKey
Write-Host "  ✅ TokenEncryption__MasterKey generada" -ForegroundColor Green

# Google OAuth
$googleId = Read-Host "Google OAuth Client ID (opcional)"
if ($googleId) {
    $env:GoogleOAuth__ClientId = $googleId
    $env:GoogleOAuth__ClientSecret = Read-Host "Google OAuth Client Secret" -AsSecureString | %{ [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($_)) }
}

# Microsoft OAuth
$msId = Read-Host "Microsoft OAuth Client ID (opcional)"
if ($msId) {
    $env:MicrosoftOAuth__ClientId = $msId
    $env:MicrosoftOAuth__ClientSecret = Read-Host "Microsoft OAuth Client Secret" -AsSecureString | %{ [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($_)) }
}

Write-Host "`n📦 Restaurando paquetes..." -ForegroundColor Yellow
dotnet restore

# ─── Tests ─────────────────────────────────────────────────────
if (-not $SkipTests) {
    Write-Host "`n🧪 Ejecutando tests..." -ForegroundColor Yellow
    $testResult = dotnet test AgendaApi.Tests -c Release --verbosity normal 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ❌ Tests fallaron. Revisa los errores." -ForegroundColor Red
        Write-Host $testResult
        exit 1
    }
    Write-Host "  ✅ Tests correctos!" -ForegroundColor Green
}

# ─── Migraciones ──────────────────────────────────────────────
if (-not $SkipMigrations) {
    Write-Host "`n📦 Aplicando migraciones..." -ForegroundColor Yellow
    dotnet ef database update --project AgendaApi.Infrastructure --startup-project AgendaApi.Api
}

Write-Host "`n✅ Setup completo!" -ForegroundColor Green
Write-Host "   API:      http://localhost:5000" -ForegroundColor Cyan
Write-Host "   Swagger:  http://localhost:5000/swagger" -ForegroundColor Cyan
Write-Host "   Health:   http://localhost:5000/health" -ForegroundColor Cyan
Write-Host "   Webhook:  POST http://localhost:5000/api/webhook`n" -ForegroundColor Cyan

dotnet run --project AgendaApi.Api
