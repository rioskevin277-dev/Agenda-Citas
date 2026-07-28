#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Flujo completo de onboarding + agendamiento para pruebas.
    Requiere: API corriendo en http://localhost:5000
#>

param([string]$BaseUrl = "http://localhost:5000")

Write-Host "🧪 AgendaApi - Test Flow" -ForegroundColor Cyan
Write-Host "=========================`n" -ForegroundColor Cyan

# 1. Crear tenant
Write-Host "1️⃣  Creando tenant..." -ForegroundColor Yellow
$tenant = Invoke-RestMethod -Uri "$BaseUrl/api/tenants" -Method Post -Body (@{
    nombre = "Peluqueria Canina Test"
    calendarProvider = "google"
} | ConvertTo-Json) -ContentType "application/json"
Write-Host "   ✅ Tenant creado: $($tenant.idTenant) - $($tenant.nombre)" -ForegroundColor Green
$tenantId = $tenant.idTenant

# 2. Agregar tipo de servicio
Write-Host "2️⃣  Agregando tipo de servicio..." -ForegroundColor Yellow
$serviceType = Invoke-RestMethod -Uri "$BaseUrl/api/tenants/$tenantId/service-types" -Method Post -Body (@{
    nombre = "Corte y bano"
    duracionMinutos = 60
    bufferMinutos = 15
    precio = 15000
} | ConvertTo-Json) -ContentType "application/json"
Write-Host "   ✅ Servicio agregado: $($serviceType.nombre) ($($serviceType.duracionMinutos)min)" -ForegroundColor Green

# 3. Verificar health
Write-Host "3️⃣  Verificando health..." -ForegroundColor Yellow
$health = Invoke-RestMethod -Uri "$BaseUrl/health"
Write-Host "   ✅ Health: $($health.status)" -ForegroundColor Green

# 4. Probar webhook simulado
Write-Host "4️⃣  Simulando webhook WhatsApp..." -ForegroundColor Yellow
$webhookPayload = @{
    object = "whatsapp_business_account"
    entry = @(@{
        id = "123"
        changes = @(@{
            value = @{
                messaging_product = "whatsapp"
                metadata = @{ phone_number_id = "123456789"; display_phone_number = "5212223334444" }
                contacts = @(@{ profile = @{ name = "Juan Perez" }; wa_id = "521234567890" })
                messages = @(@{
                    from = "521234567890"
                    id = "wamid.test.$(Get-Random -Maximum 99999)"
                    timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
                    type = "text"
                    text = @{ body = "Hola, quiero agendar un corte de pelo" }
                })
            }
            field = "messages"
        })
    })
} | ConvertTo-Json -Depth 10

try {
    $null = Invoke-RestMethod -Uri "$BaseUrl/api/webhook" -Method Post -Body $webhookPayload -ContentType "application/json"
    Write-Host "   ✅ Webhook enviado" -ForegroundColor Green
} catch {
    Write-Host "   ⚠️  Webhook: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host "`n🎯 Flujo de prueba completo!" -ForegroundColor Cyan
Write-Host "   Tenant ID: $tenantId" -ForegroundColor White
Write-Host "`n📋 Próximos pasos manuales:" -ForegroundColor Cyan
Write-Host "   - GET  $BaseUrl/api/tenants" -ForegroundColor Gray
Write-Host "   - POST $BaseUrl/api/appointments/availability (con JWT)" -ForegroundColor Gray
Write-Host "   - POST $BaseUrl/api/appointments (con JWT)" -ForegroundColor Gray
