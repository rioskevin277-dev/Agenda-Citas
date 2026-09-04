#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Monitor de Produccion de AgendaApi: prueba funcional TOTAL del API asistente.
.DESCRIPTION
    Ejecuta una bateria de pruebas funcionales sobre el API (publico y local),
    imprimiendo una tabla PASS/FAIL/WARN por prueba y un resumen final.

    Por defecto la bateria es SOLO LECTURA (no envia WhatsApp, no escribe en DB).
    Con -Full se ejecuta un ciclo de escritura sobre un tenant temporal MONITOR-.

    Salida:
        .\scripts\monitor-produccion.ps1
        .\scripts\monitor-produccion.ps1 -Full
        .\scripts\monitor-produccion.ps1 -JsonOut report.json
        .\scripts\monitor-produccion.ps1 -Full -BaseUrl http://localhost:8080

    Exit code: 0 si no hay FAIL, 1 si hay algun FAIL.
.NOTES
    Requiere un .env en la raiz del proyecto con: Jwt__Secret, DASHBOARD_KEY,
    WhatsApp__VerifyToken.
#>
param(
    [string]$BaseUrl = "https://api.adamcoia.com",
    [string]$LocalUrl = "http://localhost:8080",
    [switch]$Full,
    [string]$JsonOut = ""
)

$ErrorActionPreference = "Stop"
$ProjectRoot = "C:\Users\USUARIO\agenda-api"
$envFile = Join-Path $ProjectRoot ".env"

$results = New-Object System.Collections.Generic.List[object]
$script:firstTenantId = $null
$script:verifyToken = ""

# = Helpers de UI =
function Write-Banner($Title) {
    Write-Host ""
    Write-Host ("=" * 60) -ForegroundColor Cyan
    Write-Host "   $Title" -ForegroundColor Cyan
    Write-Host ("=" * 60) -ForegroundColor Cyan
}

function Write-ResultLine([object]$r) {
    $color = switch ($r.Result) {
        'PASS' { 'Green'; 'PASS' }
        'FAIL' { 'Red';   'FAIL' }
        'WARN' { 'Yellow';'WARN' }
        'SKIP' { 'Gray';  'SKIP' }
    }
    Write-Host ("[{0}] {1}`t- {2}" -f $color[1], $r.Name, $r.Detail) -ForegroundColor $color[0]
}

# = Invoke-Probe: ejecuta un scriptblock, captura resultado =
# Convencion WARN: dentro del scriptblock, `throw "WARN: <detalle>"` marca
# la prueba como WARN; cualquier otro throw marca FAIL. Un string de retorno = PASS.
function Invoke-Probe {
    param(
        [string]$Name,
        [scriptblock]$ScriptBlock
    )
    try {
        $detail = & $ScriptBlock
        if ($null -eq $detail) { $detail = "" }
        $r = [pscustomobject]@{ Name = $Name; Result = 'PASS'; Detail = ([string]$detail) }
    } catch {
        $msg = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
        if ($msg -like 'WARN:*') {
            $r = [pscustomobject]@{ Name = $Name; Result = 'WARN'; Detail = $msg.Substring(5).Trim() }
        } else {
            $r = [pscustomobject]@{ Name = $Name; Result = 'FAIL'; Detail = $msg }
        }
    }
    $script:results.Add($r)
    Write-ResultLine $r
    return $r
}

# = Llamada HTTP segura: lanza excepcion con status para no-2xx =
function Invoke-ProbeRequest {
    param(
        [string]$Method = "GET",
        [string]$Uri,
        [string]$Body = "",
        [string]$ContentType = "application/json",
        [hashtable]$Headers = @{}
    )
    $params = @{
        Uri = $Uri
        Method = $Method
        UseBasicParsing = $true
        TimeoutSec = 20
    }
    if ($Headers.Count -gt 0) { $params.Headers = $Headers }
    if ($Body -ne "") {
        $params.Body = $Body
        $params.ContentType = $ContentType
    }
    try {
        return Invoke-WebRequest @params
    } catch {
        # Adjuntar el body de la respuesta al error para que el detalle del FAIL sea util.
        $resp = $_.Exception.Response
        if ($resp) {
            $status = [int]$resp.StatusCode
            $msg = $_.Exception.Message
            try {
                $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
                $body = $reader.ReadToEnd()
                if ($body) { $msg = "status $status : $body" }
            } catch { $msg = "status $status : $msg" }
            throw $msg
        }
        throw
    }
}

# = Llamada HTTP que NO lanza sobre no-2xx: devuelve status+body =
function Invoke-ProbeAllowErrors {
    param(
        [string]$Method = "GET",
        [string]$Uri,
        [string]$Body = "",
        [string]$ContentType = "application/json",
        [hashtable]$Headers = @{},
        [int]$MaximumRedirection = 5
    )
    $params = @{
        Uri = $Uri
        Method = $Method
        UseBasicParsing = $true
        TimeoutSec = 20
    }
    if ($Headers.Count -gt 0) { $params.Headers = $Headers }
    if ($Body -ne "") {
        $params.Body = $Body
        $params.ContentType = $ContentType
    }
    if ($MaximumRedirection -ne 5) { $params.MaximumRedirection = $MaximumRedirection }
    try {
        return Invoke-WebRequest @params
    } catch {
        $resp = $_.Exception.Response
        if ($resp) {
            $status = [int]$resp.StatusCode
            $body = ""
            try {
                $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
                $body = $reader.ReadToEnd()
            } catch { }
            return [pscustomobject]@{ StatusCode = $status; Content = $body; Headers = @{} }
        }
        throw
    }
}

# = Zona America/Bogota a UTC (fallback UTC-5 sin DST) =
function Get-BogotaUtc {
    param([datetime]$LocalDateTime)
    try {
        $tz = [System.TimeZoneInfo]::FindSystemTimeZoneById('SA Pacific Standard Time')
        return [System.TimeZoneInfo]::ConvertTimeToUtc($LocalDateTime, $tz)
    } catch {
        return $LocalDateTime.AddHours(5)
    }
}

# = JSON array normalizado (PS 5.1: @(json | ConvertFrom-Json) anida arrays) =
function ConvertFrom-JsonList {
    param([string]$Json)
    $parsed = $Json | ConvertFrom-Json
    if ($null -eq $parsed) { return @() }
    $list = @($parsed)
    if ($list.Count -eq 1 -and $list[0] -is [System.Array]) { $list = @($list[0]) }
    return @($list)
}

# = Cargar .env =
function Load-Env {
    if (-not (Test-Path -LiteralPath $envFile)) {
        throw "No se encuentra .env en $ProjectRoot"
    }
    Get-Content -LiteralPath $envFile | ForEach-Object {
        if ($_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') {
            $k = $matches[1]; $v = $matches[2].Trim()
            if ($v -match '^"(.*)"$')  { $v = $matches[1] }
            if ($v -match "^'(.*)'$")   { $v = $matches[1] }
            Set-Item -Path "env:$k" -Value $v
        }
    }
    # No imprimir valores; solo confirmar presencia
    $missing = @('Jwt__Secret','DASHBOARD_KEY','WhatsApp__VerifyToken') | Where-Object { -not [System.Environment]::GetEnvironmentVariable($_) }
    if ($missing) {
        Write-Host "  [!] Faltan claves en .env: $($missing -join ', ')" -ForegroundColor Yellow
    }
}

# = JWT HS256 =
function ConvertTo-Base64Url([byte[]]$bytes) {
    return ([System.Convert]::ToBase64String($bytes) -replace '=+$','' -replace '\+','-' -replace '/','_')
}

function New-Jwt {
    param([string]$Secret, [string]$TenantId, [string]$Role = "admin")
    $header = '{"alg":"HS256","typ":"JWT"}'
    $headerB64 = ConvertTo-Base64Url ([System.Text.Encoding]::UTF8.GetBytes($header))

    $now = [DateTimeOffset]::UtcNow
    $exp = $now.AddMinutes(15)
    $claims = @{
        iss = "AgendaApi"
        aud = "AgendaApp"
        IdTenant = $TenantId
        Rol = $Role
        exp = $exp.ToUnixTimeSeconds()
        nbf = $now.ToUnixTimeSeconds()
        iat = $now.ToUnixTimeSeconds()
    } | ConvertTo-Json -Compress
    $payloadB64 = ConvertTo-Base64Url ([System.Text.Encoding]::UTF8.GetBytes($claims))

    $signingInput = "$headerB64.$payloadB64"
    $hmac = [System.Security.Cryptography.HMACSHA256]::new([System.Text.Encoding]::UTF8.GetBytes($Secret))
    $sigBytes = $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($signingInput))
    $sigB64 = ConvertTo-Base64Url $sigBytes
    return "$signingInput.$sigB64"
}

# = Main =
Write-Banner "AgendaApi - MONITOR DE PRODUCCION"
Write-Host "   Publico: $BaseUrl" -ForegroundColor White
Write-Host "   Local:   $LocalUrl" -ForegroundColor White
Write-Host ""
Write-Host "  Cargando .env..." -ForegroundColor Gray
try { Load-Env } catch {
    Write-Host "  [XX] $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
$script:verifyToken = [System.Environment]::GetEnvironmentVariable('WhatsApp__VerifyToken')
$dashKey = [System.Environment]::GetEnvironmentVariable('DASHBOARD_KEY')
$jwtSecret = [System.Environment]::GetEnvironmentVariable('Jwt__Secret')

if ([string]::IsNullOrWhiteSpace($script:verifyToken)) {
    Write-Host "  [!] WhatsApp__VerifyToken esta vacio; las probes D/E no son fiables." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "  ---- Bateria base (solo lectura) ----" -ForegroundColor Cyan

# A. /health local
Invoke-Probe "health local" {
    $r = Invoke-ProbeRequest -Uri "$LocalUrl/health"
    if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
    if ($r.Content -notmatch 'healthy') { throw "body no contiene 'healthy': $($r.Content)" }
    return "200 healthy"
}

# B. /health public (guardamos headers para cf-ray)
$script:cfRay = ""
Invoke-Probe "health public" {
    $r = Invoke-ProbeRequest -Uri "$BaseUrl/health"
    if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
    if ($r.Content -notmatch 'healthy') { throw "body no contiene 'healthy'" }
    $script:cfRay = [string]$r.Headers['cf-ray']
    $detail = "200 healthy"
    if ($script:cfRay) { $detail += " (cf-ray: $script:cfRay)" }
    return $detail
}

# C. Swagger public
Invoke-Probe "swagger public" {
    $r = Invoke-ProbeRequest -Uri "$BaseUrl/swagger/v1/swagger.json"
    if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
    if ($r.Content -notmatch 'openapi') { throw "body no contiene 'openapi'" }
    return "200 openapi"
}

# D. Webhook verify (token correcto)
Invoke-Probe "webhook verify GET" {
    if ([string]::IsNullOrWhiteSpace($script:verifyToken)) { throw "WhatsApp__VerifyToken vacio" }
    $u = "$BaseUrl/api/v1/webhook?hub.mode=subscribe&hub.verify_token=$([uri]::EscapeDataString($script:verifyToken))&hub.challenge=12345"
    $r = Invoke-ProbeRequest -Uri $u
    if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
    if ($r.Content.Trim() -ne "12345") { throw "challenge no devuelto (body='$($r.Content.Trim())')" }
    return "200 challenge=$($r.Content.Trim())"
}

# E. Webhook verify (token incorrecto)
Invoke-Probe "webhook verify wrong-token" {
    $u = "$BaseUrl/api/v1/webhook?hub.mode=subscribe&hub.verify_token=wrongtoken&hub.challenge=12345"
    $r = Invoke-ProbeAllowErrors -Uri $u
    if ($r.StatusCode -eq 200) { throw "200 (esperaba rechazo 403)" }
    if ($r.StatusCode -eq 500) { throw "WARN: 500 (esperaba 403)" }
    if ($r.StatusCode -ne 403) { throw "WARN: status $($r.StatusCode) (esperado 403)" }
    return "403 rechazado"
}

# F. Webhook POST no-op
Invoke-Probe "webhook POST noop" {
    $payload = '{"object":"whatsapp_business_account","entry":[{"id":"0","changes":[{"value":{"messaging_product":"whatsapp","metadata":{"display_phone_number":"+00000000000","phone_number_id":"000000000000000"},"contacts":[{"wa_id":"00000000000"}],"messages":[{"from":"00000000000","id":"wamid.MONITOR_NOOP","timestamp":"0","type":"text","text":{"body":"monitor noop"}}]},"field":"messages"}]}]}'
    $r = Invoke-ProbeRequest -Method POST -Uri "$BaseUrl/api/v1/webhook" -Body $payload
    if ($r.StatusCode -ge 500) { throw "status $($r.StatusCode)" }
    if ($r.StatusCode -ne 200) { throw "WARN: status $($r.StatusCode) (esperado 200)" }
    return "200 sin outbound"
}

# G. Dashboard page (optional key)
Invoke-Probe "dashboard page html" {
    $r = Invoke-ProbeRequest -Uri "$BaseUrl/api/v1/dashboard/conversations/page"
    if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
    $ct = [string]$r.Headers['Content-Type']
    if ($ct -notmatch 'text/html') { throw "WARN: 200 con ct='$ct' (esperado text/html)" }
    return "200 text/html"
}

# H. Dashboard conversations (key correcta)
Invoke-Probe "dashboard conversations" {
    $u = "$BaseUrl/api/v1/dashboard/conversations?key=$([uri]::EscapeDataString($dashKey))&limit=5"
    $r = Invoke-ProbeRequest -Uri $u
    if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
    try { $null = $r.Content | ConvertFrom-Json } catch { throw "no JSON" }
    return "200 JSON"
}

# I. Dashboard conversations (wrong key)
Invoke-Probe "dashboard wrong-key" {
    $u = "$BaseUrl/api/v1/dashboard/conversations?key=wrongkey&limit=5"
    $r = Invoke-ProbeAllowErrors -Uri $u
    if ($r.StatusCode -eq 200) { throw "200 (esperaba 401)" }
    if ($r.StatusCode -ne 401) { throw "WARN: status $($r.StatusCode) (esperado 401)" }
    return "401 rechazado"
}

# J. Dashboard failures
Invoke-Probe "dashboard failures" {
    $u = "$BaseUrl/api/v1/dashboard/failures?key=$([uri]::EscapeDataString($dashKey))&limit=5"
    $r = Invoke-ProbeRequest -Uri $u
    if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
    $arr = @($r.Content | ConvertFrom-Json)
    if ($arr.Count -eq 0) { return "200 (array vacio)" }
    return "200 ($($arr.Count) registros)"
}

# K. JWT + tenants: captura primer IdTenant y RE-emite bearer con ese tenant real
$script:bearer = ""
Invoke-Probe "jwt + tenants" {
    if ([string]::IsNullOrWhiteSpace($jwtSecret)) { throw "Jwt__Secret vacio" }
    $probeTenantId = if ($script:firstTenantId) { $script:firstTenantId } else { [guid]::Empty.ToString() }
    $jwt = New-Jwt -Secret $jwtSecret -TenantId $probeTenantId
    $r = Invoke-ProbeRequest -Uri "$BaseUrl/api/v1/tenants" -Headers @{ Authorization = "Bearer $jwt" }
    if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
    $arr = $r.Content | ConvertFrom-Json
    if ($arr.Count -eq 0) { throw "array vacio (no hay tenants que probar)" }
    # Evitar elegir un tenant de PRUEBA propio del monitor como "primer tenant"
    $real = @($arr | Where-Object { $_.Nombre -notlike 'MONITOR-PRUEBA-*' })
    if ($real.Count -eq 0) { $real = @($arr) }
    $script:firstTenantId = [string]$real[0].IdTenant
    # Re-emitir con el tenant REAL: los endpoints scoped exigen IdTenant valido en el claim
    $script:bearer = New-Jwt -Secret $jwtSecret -TenantId $script:firstTenantId
    return "200 ($($arr.Count) tenants; primer IdTenant=$script:firstTenantId)"
}

# = A partir de aqui usamos el JWT con tenant real =
if ($script:firstTenantId) {
    if (-not $script:bearer) {
        $script:bearer = New-Jwt -Secret $jwtSecret -TenantId $script:firstTenantId
    }

    # L. summary
    Invoke-Probe "dashboard summary (bearer)" {
        $r = Invoke-ProbeRequest -Uri "$BaseUrl/api/v1/dashboard/summary" -Headers @{ Authorization = "Bearer $script:bearer" }
        if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
        if ($r.Content -notmatch 'Totales') { throw "body no contiene 'Totales'" }
        return "200"
    }

    # M. clients
    Invoke-Probe "clients list (bearer)" {
        $r = Invoke-ProbeRequest -Uri "$BaseUrl/api/v1/clients?q=" -Headers @{ Authorization = "Bearer $script:bearer" }
        if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
        $arr = $r.Content | ConvertFrom-Json
        return "200 ($($arr.Count))"
    }

    # N. appointments (rango)
    Invoke-Probe "appointments range (bearer)" {
        $from = [DateTimeOffset]::UtcNow.AddDays(-7).ToString("o")
        $to   = [DateTimeOffset]::UtcNow.AddDays(7).ToString("o")
        $u = "$BaseUrl/api/v1/appointments?from=$([uri]::EscapeDataString($from))&to=$([uri]::EscapeDataString($to))"
        $r = Invoke-ProbeRequest -Uri $u -Headers @{ Authorization = "Bearer $script:bearer" }
        if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
        $arr = $r.Content | ConvertFrom-Json
        return "200 ($($arr.Count))"
    }

    # O. appointment availability (captura slots para el ciclo -Full)
    $script:realAvailSlots = @()
    Invoke-Probe "appointment availability (bearer)" {
        $f1 = [DateTime]::UtcNow.AddDays(1).ToString("yyyy-MM-dd")
        $f2 = [DateTime]::UtcNow.AddDays(7).ToString("yyyy-MM-dd")
        $u = "$BaseUrl/api/v1/appointments/availability?fechaInicio=$f1&fechaFin=$f2"
        $r = Invoke-ProbeRequest -Uri $u -Headers @{ Authorization = "Bearer $script:bearer" }
        if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
        $script:realAvailSlots = @(ConvertFrom-JsonList $r.Content)
        return "200 ($($script:realAvailSlots.Count))"
    }

    # P. service-types del tenant (captura primer id)
    $script:realServiceId = $null
    Invoke-Probe "service-types (tenant)" {
        $r = Invoke-ProbeRequest -Uri "$BaseUrl/api/v1/tenants/$script:firstTenantId/service-types" -Headers @{ Authorization = "Bearer $script:bearer" }
        if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
        $arr = $r.Content | ConvertFrom-Json
        if ($arr.Count -gt 0) { $script:realServiceId = [string]$arr[0].IdServiceType }
        return "200 ($($arr.Count))"
    }

    # Q. professionals del tenant (captura primer id)
    $script:realProfId = $null
    Invoke-Probe "professionals (tenant)" {
        $r = Invoke-ProbeRequest -Uri "$BaseUrl/api/v1/tenants/$script:firstTenantId/professionals" -Headers @{ Authorization = "Bearer $script:bearer" }
        if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
        $arr = $r.Content | ConvertFrom-Json
        if ($arr.Count -gt 0) { $script:realProfId = [string]$arr[0].IdProfessional }
        return "200 ($($arr.Count))"
    }

    # R. OAuth authorize (302 -> 200 final; 500 = no configurado; no core)
    Invoke-Probe "oauth google authorize" {
        $u = "$BaseUrl/api/v1/oauth/google/authorize?tenantId=$script:firstTenantId"
        $r = Invoke-ProbeAllowErrors -Uri $u
        if ($r.StatusCode -eq 200) { return "302 redirigido -> 200" }
        if ($r.StatusCode -eq 302) { return "302 no seguido" }
        if ($r.StatusCode -eq 500) { throw "WARN: 500 (OAuth no configurado)" }
        throw "status $($r.StatusCode) (se esperaba redirect 200)"
    }
    Invoke-Probe "oauth microsoft authorize" {
        $u = "$BaseUrl/api/v1/oauth/microsoft/authorize?tenantId=$script:firstTenantId"
        $r = Invoke-ProbeAllowErrors -Uri $u
        if ($r.StatusCode -eq 200) { return "302 redirigido -> 200" }
        if ($r.StatusCode -eq 302) { return "302 no seguido" }
        if ($r.StatusCode -eq 500) { throw "WARN: 500 (OAuth no configurado)" }
        throw "status $($r.StatusCode) (se esperaba redirect 200)"
    }

    # A2. Integridad de datos: todo profesional del tenant real debe tener >=1 servicio
    #     vinculado (profesional_services.activo=1). No existe endpoint HTTP para
    #     listarlo -> sola lectura via docker sqlcmd. Degrada a WARN si no hay SQL_PASSWORD.
    Invoke-Probe "vinculo professional-servicio" {
        $sqp = [System.Environment]::GetEnvironmentVariable('SQL_PASSWORD')
        if (-not $sqp) { throw "WARN: SQL_PASSWORD no esta en .env; integridad no verificada" }
        $sql = @"
SET QUOTED_IDENTIFIER ON;
SELECT COUNT(*) AS total FROM (
  SELECT p.id_professional, p.nombre AS prof,
         COUNT(st.id_service_type) AS serv_ok
  FROM professionals p
  LEFT JOIN professional_services ps ON ps.id_professional = p.id_professional AND ps.activo = 1
  LEFT JOIN service_types st ON st.id_service_type = ps.id_service_type AND st.id_tenant = p.id_tenant
  WHERE p.id_tenant = '$($script:firstTenantId)'
  GROUP BY p.id_professional, p.nombre
) x WHERE x.serv_ok = 0;
"@
        $out = & docker exec agenda-sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $sqp -C -d AgendaApi -Q $sql -W 2>&1
        if ($LASTEXITCODE -ne 0) { throw "WARN: docker exec sqlcmd fallo ($LASTEXITCODE): $($out -join ' ')" }
        $orphans = ($out | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^\d+$' } | Select-Object -Last 1)
        if ($orphans -eq '0') { return "200 OK (0 profesionales sin servicio)" }
        throw "FAIL: $orphans profesionales sin servicio => no pueden agendar (revisar professional_services)"
    }

    # = -Full: ciclo de escritura sobre tenant MONITOR- =
    if ($Full) {
        Write-Host ""
        Write-Host "  ---- Ciclo -Full (escritura sobre tenant MONITOR-) ----" -ForegroundColor Cyan

        $stamp = (Get-Date -Format "yyyyMMddHHmmss")
        $newTenantId = $null
        $newServiceId = $null
        $newProfId = $null
        $newAppId = $null

        # S. limpieza previa de tenants MONITOR-PRUEBA-* (via docker exec sqlcmd)
        Invoke-Probe "S limpiar MONITOR previos" {
            $sqp = [System.Environment]::GetEnvironmentVariable('SQL_PASSWORD')
            if (-not $sqp) { throw "WARN: SQL_PASSWORD no esta en .env; limpieza manual" }
            # sqlcmd no activa QUOTED_IDENTIFIER por defecto -> Msg 1934 en DELETE; forzarlo.
            # Orden de borrado por dependencias: appointments tienen FK -> clients (NO CASCADE).
            $sql = @"
SET QUOTED_IDENTIFIER ON;
DELETE FROM appointments WHERE id_tenant IN (SELECT id_tenant FROM tenants WHERE nombre LIKE 'MONITOR-PRUEBA-%');
DELETE FROM clients WHERE id_tenant IN (SELECT id_tenant FROM tenants WHERE nombre LIKE 'MONITOR-PRUEBA-%');
DELETE FROM tenants WHERE nombre LIKE 'MONITOR-PRUEBA-%';
SELECT COUNT(*) AS restantes FROM tenants WHERE nombre LIKE 'MONITOR-PRUEBA-%';
"@
            $out = & docker exec agenda-sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $sqp -C -d AgendaApi -Q $sql -W 2>&1
            if ($LASTEXITCODE -ne 0) { throw "WARN: docker exec sqlcmd fallo ($LASTEXITCODE): $($out -join ' ')" }
            $deleted = ($out | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^\d+$' } | Select-Object -Last 1)
            if (-not $deleted) { $deleted = '?' }
            return "200 (restantes: $deleted)"
        }

        # T. crear tenant
        Invoke-Probe "T crear tenant MONITOR" {
            $body = (@{ Nombre = "MONITOR-PRUEBA-$stamp"; WhatsAppPhoneNumberId = "000000000000000" }) | ConvertTo-Json -Compress
            $r = Invoke-ProbeRequest -Method POST -Uri "$BaseUrl/api/v1/tenants" -Body $body -Headers @{ Authorization = "Bearer $script:bearer" }
            if ($r.StatusCode -ne 201) { throw "status $($r.StatusCode)" }
            $obj = $r.Content | ConvertFrom-Json
            $script:newTenantId = [string]$obj.IdTenant
            return "201 IdTenant=$script:newTenantId"
        }

        if ($script:newTenantId) {
            $tenantAuth = @{ Authorization = "Bearer $script:bearer" }

            # U. service-type
            Invoke-Probe "U crear service-type" {
                $body = (@{ Nombre = "Servicio Monitor"; DuracionMinutos = 30; BufferMinutos = 5; Precio = 100 }) | ConvertTo-Json -Compress
                $r = Invoke-ProbeRequest -Method POST -Uri "$BaseUrl/api/v1/tenants/$script:newTenantId/service-types" -Body $body -Headers $tenantAuth
                if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
                $obj = $r.Content | ConvertFrom-Json
                $script:newServiceId = [string]$obj.id
                return "200 id=$script:newServiceId"
            }

            # V. professional
            Invoke-Probe "V crear professional" {
                $body = (@{ Nombre = "Prof Monitor"; ServiceTypeIds = @($script:newServiceId) }) | ConvertTo-Json -Compress
                $r = Invoke-ProbeRequest -Method POST -Uri "$BaseUrl/api/v1/tenants/$script:newTenantId/professionals" -Body $body -Headers $tenantAuth
                if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
                $obj = $r.Content | ConvertFrom-Json
                $script:newProfId = [string]$obj.id
                return "200 id=$script:newProfId"
            }

            # W. re-leer service-types (en lugar de cliente-http, que no existe)
            Invoke-Probe "W re-leer service-types" {
                $r = Invoke-ProbeRequest -Uri "$BaseUrl/api/v1/tenants/$script:newTenantId/service-types" -Headers $tenantAuth
                if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
                $arr = $r.Content | ConvertFrom-Json
                return "200 ($($arr.Count))"
            }

            # V2. seed de availability_rules para el tenant MONITOR- (no existe endpoint HTTP):
            #    6 reglas lun-sab 07:00-19:00 hora local (igual al tenant real), id_professional=NULL,
            #    id_availability_rule=NEWID(). Necesario para que el ciclo X-Z pruebe disponibilidad.
            Invoke-Probe "V2 seed disponibilidad" {
                $sqp = [System.Environment]::GetEnvironmentVariable('SQL_PASSWORD')
                if (-not $sqp) { throw "WARN: SQL_PASSWORD no esta en .env; ciclo X-Z omitido (sin disponibilidad)" }
                if (-not $script:newTenantId) { throw "WARN: sin newTenantId; se omite el seed" }
                $sql = @"
SET QUOTED_IDENTIFIER ON;
DECLARE @tid uniqueidentifier = '$($script:newTenantId)';
IF NOT EXISTS (SELECT 1 FROM availability_rules WHERE id_tenant=@tid) BEGIN
INSERT INTO availability_rules (id_availability_rule, id_tenant, dia_semana, hora_inicio, hora_fin, activo) VALUES
 (NEWID(),@tid,1,'07:00:00','19:00:00',1),
 (NEWID(),@tid,2,'07:00:00','19:00:00',1),
 (NEWID(),@tid,3,'07:00:00','19:00:00',1),
 (NEWID(),@tid,4,'07:00:00','19:00:00',1),
 (NEWID(),@tid,5,'07:00:00','19:00:00',1),
 (NEWID(),@tid,6,'07:00:00','19:00:00',1);
END
SELECT COUNT(*) AS rules FROM availability_rules WHERE id_tenant=@tid;
"@
                $out = & docker exec agenda-sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $sqp -C -d AgendaApi -Q $sql -W 2>&1
                if ($LASTEXITCODE -ne 0) { throw "WARN: docker exec sqlcmd fallo ($LASTEXITCODE): $($out -join ' ')" }
                $cnt = $out | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^\d+$' } | Select-Object -Last 1
                return "200 (reglas: $cnt)"
            }

            # X-Y-Z-AA. Ciclo de CITA auto-contenido sobre el tenant MONITOR- (descarta la base; el
            #    bearer se re-emite con IdTenant=newTenantId; los slots se consultan con ese mismo bearer).
            $script:newAppId = $null
            if (-not $script:newTenantId -or -not $script:newServiceId -or -not $script:newProfId) {
                Write-Host "  [!!] Sin tenant/service/professional MONITOR; se omiten X-Z." -ForegroundColor Yellow
            } else {
                $newBearer = New-Jwt -Secret $jwtSecret -TenantId $script:newTenantId
                $newAuth = @{ Authorization = "Bearer $newBearer" }

                # W2. re-leer availability del tenant MONITOR- (debe devolver los slots de las reglas V2)
                $script:newAvailSlots = @()
                Invoke-Probe "W2 availability MONITOR" {
                    $f1 = [DateTime]::UtcNow.AddDays(1).ToString("yyyy-MM-dd")
                    $f2 = [DateTime]::UtcNow.AddDays(7).ToString("yyyy-MM-dd")
                    $u = "$BaseUrl/api/v1/appointments/availability?fechaInicio=$f1&fechaFin=$f2"
                    $r = Invoke-ProbeRequest -Uri $u -Headers $newAuth
                    if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
                    $script:newAvailSlots = @(ConvertFrom-JsonList $r.Content)
                    return "200 ($($script:newAvailSlots.Count) slots)"
                }

                if ($script:newAvailSlots.Count -gt 0) {
                    # X. crear appointment en el primer slot (hora LOCAL = UTC+5; la regla es hora Bogota)
                    Invoke-Probe "X crear appointment" {
                        $slot = $script:newAvailSlots[0]
                        $startLocal = ([DateTime]$slot.start).AddHours(5)
                        $body = (@{
                            TenantId       = $script:newTenantId
                            ServiceTypeId  = $script:newServiceId
                            ProfessionalId = $script:newProfId
                            ClientName     = "Monitor-PRUEBA-$stamp"
                            ClientWhatsApp = "+00000000000"
                            FechaInicio    = $startLocal.ToString("o")
                            FechaFin       = $startLocal.AddMinutes(30).ToString("o")
                        }) | ConvertTo-Json -Compress
                        $r = Invoke-ProbeRequest -Method POST -Uri "$BaseUrl/api/v1/appointments" -Body $body -Headers $newAuth
                        if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
                        $obj = $r.Content | ConvertFrom-Json
                        $script:newAppId = [string]$obj.id
                        return "200 id=$script:newAppId (slot $startLocal)"
                    }

                    if ($script:newAppId) {
                        # Y. reschedule a otro slot disponible
                        Invoke-Probe "Y reschedule appointment" {
                            $y1 = [DateTime]::UtcNow.AddDays(1).ToString("yyyy-MM-dd")
                            $y2 = [DateTime]::UtcNow.AddDays(7).ToString("yyyy-MM-dd")
                            $u = "$BaseUrl/api/v1/appointments/availability?fechaInicio=$y1&fechaFin=$y2"
                            $r2 = Invoke-ProbeRequest -Uri $u -Headers $newAuth
                            $newSlots = @(ConvertFrom-JsonList $r2.Content) | Where-Object { [DateTime]$_.start -ne [DateTime]$script:newAvailSlots[0].start }
                            if ($newSlots.Count -eq 0) { throw "WARN: sin slot alternativo para reschedule" }
                            $target = $newSlots[0]
                            $targetLocal = ([DateTime]$target.start).AddHours(5)
                            $body = (@{ TenantId = $script:newTenantId; NuevaFechaInicio = $targetLocal.ToString("o") }) | ConvertTo-Json -Compress
                            $r = Invoke-ProbeRequest -Method PUT -Uri "$BaseUrl/api/v1/appointments/$script:newAppId/reschedule" -Body $body -Headers $newAuth
                            if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
                            return "200 -> $targetLocal"
                        }

                        # Z. cancel
                        Invoke-Probe "Z cancel appointment" {
                            $body = (@{ TenantId = $script:newTenantId }) | ConvertTo-Json -Compress
                            $r = Invoke-ProbeRequest -Method POST -Uri "$BaseUrl/api/v1/appointments/$script:newAppId/cancel" -Body $body -Headers $newAuth
                            if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
                            return "200"
                        }

                        # AA. leer appointment (status final)
                        Invoke-Probe "AA get appointment" {
                            $r = Invoke-ProbeRequest -Uri "$BaseUrl/api/v1/appointments/$script:newAppId" -Headers $newAuth
                            if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
                            $obj = $r.Content | ConvertFrom-Json
                            return "200 status=$($obj.Status)"
                        }
                    }
                }
            }
        }

        Write-Host ""
        Write-Host "  [!!] LIMPIEZA MANUAL (no existe HTTP DELETE):" -ForegroundColor Yellow
        if ($script:newTenantId) {
            Write-Host "       - Tenant MONITOR + citas/cliente de prueba: DELETE FROM Tenants WHERE IdTenant='$script:newTenantId' (los cascades cubren availability_rules, service_types, professionals, appointments, clients)." -ForegroundColor Yellow
        }
    }
} else {
    Write-Host ""
    Write-Host "  [!!] No se pudo obtener un primer tenant (probe K). Se omiten las pruebas autenticadas L-AA." -ForegroundColor Yellow
}

<# = Resumen = #>
Write-Host ""
Write-Banner "RESUMEN"
$pass = ($script:results | Where-Object { $_.Result -eq 'PASS' }).Count
$fail = ($script:results | Where-Object { $_.Result -eq 'FAIL' }).Count
$warn = ($script:results | Where-Object { $_.Result -eq 'WARN' }).Count
$skip = ($script:results | Where-Object { $_.Result -eq 'SKIP' }).Count

Write-Host ("  PASS: {0}" -f $pass) -ForegroundColor Green
Write-Host ("  FAIL: {0}" -f $fail) -ForegroundColor Red
Write-Host ("  WARN: {0}" -f $warn) -ForegroundColor Yellow
Write-Host ("  SKIP: {0}" -f $skip) -ForegroundColor Gray

if ($fail -gt 0) {
    $failed = $script:results | Where-Object { $_.Result -eq 'FAIL' }
    Write-Host ""
    Write-Host "  Pruebas FALLADAS:" -ForegroundColor Red
    foreach ($f in $failed) {
        Write-Host ("    - {0}: {1}" -f $f.Name, $f.Detail) -ForegroundColor Red
    }
}

# = JSON report =
if ($JsonOut) {
    try {
        $report = [pscustomobject]@{
            timestamp = [DateTime]::UtcNow.ToString("o")
            baseUrl   = $BaseUrl
            results   = @($script:results | ForEach-Object {
                [pscustomobject]@{ name = $_.Name; result = $_.Result; detail = $_.Detail }
            })
            counts    = [pscustomobject]@{ pass = $pass; fail = $fail; warn = $warn; skip = $skip }
        }
        $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $JsonOut -Encoding UTF8
        Write-Host ""
        Write-Host "  Reporte JSON: $JsonOut" -ForegroundColor Gray
    } catch {
        Write-Host "  [!] No se pudo escribir $JsonOut : $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host ""
if ($fail -gt 0) {
    Write-Host "  Estado: FALLO ($fail fallos)" -ForegroundColor Red
    exit 1
} else {
    Write-Host "  Estado: OK" -ForegroundColor Green
    exit 0
}
