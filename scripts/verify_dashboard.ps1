# Script de verificación end-to-end del endpoint del dashboard.
# Prerrequisitos: JWT_SECRET en .env (solo lectura), entorno con `curl`.
# Uso: powershell -ExecutionPolicy Bypass -File scripts\verify_dashboard.ps1

$ErrorActionPreference = 'Stop'
$Base = 'https://api.adamcoia.com'

# --- Lee el JWT secret desde .env (línea "Jwt__Secret=..." o "JWT_SECRET=...") ---
$envPath = Join-Path $PSScriptRoot '..\.env'
$secret = $null
Get-Content $envPath | ForEach-Object {
    if ($_ -match '^Jwt__Secret=(.+)$') { $secret = $matches[1].Trim() }
    elseif ($_ -match '^JWT_SECRET=(.+)$') { if (-not $secret) { $secret = $matches[1].Trim() } }
}
if (-not $secret) { throw 'No se encontro JWT_SECRET en .env' }
Write-Host "[ok] JWT secret cargado ($($secret.Length) chars)" -ForegroundColor Green

# --- Función para crear un JWT HS256 firmado ---
function B64Url([byte[]]$inputBytes) {
    return [Convert]::ToBase64String($inputBytes).TrimEnd('=').Replace('+','-').Replace('/','_')
}
function New-Jwt([hashtable]$claims) {
    $header = @{ alg = 'HS256'; typ = 'JWT' } | ConvertTo-Json -Compress
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $payload = @{
        iss = 'AgendaApi'
        aud = 'AgendaApp'
        exp = $now + 3600
        nbf = $now
        iat = $now
    }
    foreach ($k in $claims.Keys) { $payload[$k] = $claims[$k] }
    $headerB64 = B64Url ([Text.Encoding]::UTF8.GetBytes($header))
    $payloadB64 = B64Url ([Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Compress)))
    $data = "$headerB64.$payloadB64"
    $key = [Text.Encoding]::UTF8.GetBytes($secret)
    $hmac = [Security.Cryptography.HMACSHA256]::new($key)
    $sig = B64Url ($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($data)))
    return "$data.$sig"
}

# --- 1) Health ---
Write-Host "`n--- 1) Health ---"
$health = curl.exe -s -w "|%{http_code}" "$Base/health"
Write-Host $health

# --- 2) Sin token -> 401 (ruta protegida) ---
Write-Host "`n--- 2) Dashboard sin token (espera 401) ---"
$noAuth = curl.exe -s -o NUL -w "%{http_code}" "$Base/api/v1/dashboard/summary"
Write-Host "HTTP $noAuth"

# --- 3) JWT sin claim IdTenant -> 401 "Tenant no configurado" ---
Write-Host "`n--- 3) Dashboard con JWT sin tenant (espera 401 o error de tenant) ---"
$anon = New-Jwt @{}
$noClaim = curl.exe -s -w "`nHTTP %{http_code}`n" -H "Authorization: Bearer $anon" "$Base/api/v1/dashboard/summary"
Write-Host $noClaim

# --- 4) Listar tenants para obtener un IdTenant real ---
Write-Host "`n--- 4) Listar tenants (para un IdTenant real) ---"
$tenants = curl.exe -s -H "Authorization: Bearer $anon" "$Base/api/v1/tenants"
Write-Host $tenants
$tenantsArr = @($tenants | ConvertFrom-Json)
$idTenant = if ($tenantsArr.Count -gt 0) { "$($tenantsArr[0].idTenant)" } else { $null }
if (-not $idTenant) {
    Write-Warning 'No hay tenants para probar el dashboard con datos. Fin de la verificacion (sin tenant).'
    exit 0
}
Write-Host "Usando IdTenant: $idTenant"

# --- 5) JWT con IdTenant -> 200 + KPIs ---
Write-Host "`n--- 5) Dashboard con tenant (espera 200 + KPIs) ---"
$authed = New-Jwt @{ IdTenant = "$idTenant" }
$res = curl.exe -s -w "`nHTTP %{http_code}`n" -H "Authorization: Bearer $authed" "$Base/api/v1/dashboard/summary?fechaDesde=2026-07-01&fechaHasta=2026-08-14"
Write-Host $res

Write-Host "`nVerificacion completa." -ForegroundColor Green