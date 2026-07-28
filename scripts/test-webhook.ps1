#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Simula un webhook de WhatsApp para pruebas locales.
    Envia un payload de ejemplo al endpoint /api/webhook.
#>

param(
    [string]$BaseUrl = "http://localhost:5000",
    [string]$From = "521234567890",
    [string]$Message = "Hola, quiero agendar una cita",
    [string]$PhoneNumberId = "123456789"
)

$payload = @{
    object = "whatsapp_business_account"
    entry = @(
        @{
            id = "123456789"
            changes = @(
                @{
                    value = @{
                        messaging_product = "whatsapp"
                        metadata = @{
                            phone_number_id = $PhoneNumberId
                            display_phone_number = "5212223334444"
                        }
                        contacts = @(
                            @{
                                profile = @{ name = "Cliente Test" }
                                wa_id = $From
                            }
                        )
                        messages = @(
                            @{
                                from = $From
                                id = "wamid.$(Get-Random -Maximum 999999)"
                                timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
                                type = "text"
                                text = @{ body = $Message }
                            }
                        )
                    }
                    field = "messages"
                }
            )
        }
    )
}

$json = $payload | ConvertTo-Json -Depth 10

Write-Host "📤 Enviando webhook simulado..." -ForegroundColor Cyan
Write-Host "   De: $From" -ForegroundColor Gray
Write-Host "   Mensaje: $Message" -ForegroundColor Gray
Write-Host "   PhoneNumberId: $PhoneNumberId`n" -ForegroundColor Gray

try {
    $response = Invoke-RestMethod -Uri "$BaseUrl/api/webhook" -Method Post -Body $json -ContentType "application/json"
    Write-Host "✅ Webhook enviado exitosamente" -ForegroundColor Green
}
catch {
    Write-Host "❌ Error: $_" -ForegroundColor Red
    if ($_.Exception.Response) {
        $reader = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
        Write-Host "   Response: $($reader.ReadToEnd())" -ForegroundColor Red
    }
}
