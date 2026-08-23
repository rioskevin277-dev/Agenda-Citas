using System.Globalization;
using AgendaApi.Domain.Entities;
using AgendaApi.Domain.Ports;
using AgendaApi.Domain.Services;
using Microsoft.Extensions.Logging;

namespace AgendaApi.Infrastructure.Services;

/// <summary>
/// Memoria operativa del cliente para ADAM. Compila el contexto CRM (perfil, estado,
/// etiquetas, notas, historial de citas, servicios consumidos, última y próxima cita)
/// desde la BD y lo devuelve como un bloque de texto listo para inyectar en el system
/// prompt del AI. También mantiene los campos operativos del cliente (UltimaInteraccion,
/// ProximaCita, Estado) al día.
/// </summary>
public class ClientContextService
{
    private readonly IClientRepository _clientRepo;
    private readonly IAppointmentRepository _appointmentRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ClientContextService> _logger;

    public ClientContextService(
        IClientRepository clientRepo,
        IAppointmentRepository appointmentRepo,
        IUnitOfWork unitOfWork,
        ILogger<ClientContextService> logger)
    {
        _clientRepo = clientRepo;
        _appointmentRepo = appointmentRepo;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    /// <summary>
    /// Compila el contexto CRM del cliente para la IA. Resuelve (o crea) el cliente por su
    /// identificador canónico — BSUID (userId) primero, teléfono como fallback para clientes
    /// legacy — y devuelve el bloque de contexto base (o el historial si ya existe).
    /// </summary>
    public async Task<string> BuildClientContextAsync(
        Guid tenantId,
        string userId,
        CancellationToken ct = default,
        string? clientName = null,
        string? phone = null,
        string? username = null)
    {
        var (client, created) = await ResolveOrCreateAsync(tenantId, userId, ct, clientName, phone, username);

        // Cliente recién creado (resolución marcada): ya se guardó en BD, devolvemos el bloque base
        // sin gastar una query de historial (no tiene citas).
        if (created)
            return BuildBlock(client, new List<Appointment>(), 0, DateTime.UtcNow);

        // Actualizar la interacción del cliente cada vez que escribe.
        client.UltimaInteraccion = DateTime.UtcNow;
        client.FechaActualizacion = DateTime.UtcNow;

        // Si el cliente aún no tiene nombre y el webhook lo trae, se persiste (mejora el CRM).
        if (string.IsNullOrWhiteSpace(client.Nombre) && !string.IsNullOrWhiteSpace(clientName))
            client.Nombre = clientName;

        // Historial de citas del cliente para derivar contexto.
        var appointments = await _appointmentRepo.GetByClientIdAsync(client.IdClient, ct);

        // Recalcular estado del cliente y próxima cita a partir de su historial
        // (helper compartido del CRM, fuente única de verdad).
        ClientStateCalculator.ApplyDerivedState(client, appointments, DateTime.UtcNow);

        await _clientRepo.UpdateAsync(client, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return BuildBlock(client, appointments, appointments.Count, DateTime.UtcNow);
    }

    /// <summary>
    /// Resuelve el cliente por su identificador canónico o lo crea. Orden:
    /// 1) por BSUID (userId); 2) si no y hay teléfono, por teléfono legacy y se le VINCULA el
    /// userId (la misma persona migró de teléfono a BSUID: mantenemos su historial); 3) si no,
    /// se crea como 'nuevo'. Devuelve <c>(client, created)</c>.
    /// </summary>
    public async Task<(Client Client, bool Created)> ResolveOrCreateAsync(
        Guid tenantId,
        string userId,
        CancellationToken ct = default,
        string? clientName = null,
        string? phone = null,
        string? username = null)
    {
        Client? client = null;

        if (!string.IsNullOrWhiteSpace(userId))
            client = await _clientRepo.GetByUserIdAsync(userId, tenantId, ct);

        if (client == null && !string.IsNullOrWhiteSpace(phone))
        {
            client = await _clientRepo.GetByWhatsAppAsync(phone, tenantId, ct);
            if (client != null && !string.IsNullOrWhiteSpace(userId) && client.UserId != userId)
            {
                // Merge legacy → BSUID: vinculamos el user_id al cliente que ya existía por teléfono.
                client.UserId = userId;
                client.FechaActualizacion = DateTime.UtcNow;
                await _clientRepo.UpdateAsync(client, ct);
                await _unitOfWork.SaveChangesAsync(ct);
                _logger.LogInformation("[CRM] Cliente {Phone} vinculado a BSUID {UserId} (tenant {Tenant})",
                    phone, userId, tenantId);
            }
        }

        if (client == null)
        {
            client = new Client
            {
                IdClient = Guid.NewGuid(),
                IdTenant = tenantId,
                UserId = string.IsNullOrWhiteSpace(userId) ? null : userId,
                WhatsApp = phone ?? "",
                Username = string.IsNullOrWhiteSpace(username) ? null : username,
                Nombre = string.IsNullOrWhiteSpace(clientName) ? null : clientName,
                Estado = "nuevo",
                Activo = true,
                UltimaInteraccion = DateTime.UtcNow
            };
            await _clientRepo.CreateAsync(client, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            _logger.LogInformation("[CRM] Cliente {Partner} creado (nuevo) en tenant {Tenant}",
                client.PartnerId, tenantId);
            return (client, true);
        }

        // Persistir username si el webhook lo trae y el cliente no lo tenía.
        if (!string.IsNullOrWhiteSpace(username) && client.Username != username)
        {
            client.Username = username;
            client.FechaActualizacion = DateTime.UtcNow;
            await _clientRepo.UpdateAsync(client, ct);
            await _unitOfWork.SaveChangesAsync(ct);
        }

        return (client, false);
    }

    /// <summary>
    /// Continuidad de conversación ante system message user_changed_number / user_changed_user_id:
    /// el usuario cambió su BSUID (previous_user_id → user_id). Reasigna el client para no perder
    /// su historial de citas ni la conversación.
    /// </summary>
    public async Task HandleUserChangedIdAsync(Guid tenantId, string newUserId, string previousUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newUserId) || string.IsNullOrWhiteSpace(previousUserId))
            return;

        var client = await _clientRepo.GetByUserIdAsync(previousUserId, tenantId, ct);
        if (client == null)
        {
            _logger.LogInformation("[CRM] user_changed sin client previo {Prev} (tenant {Tenant}): nada que reasignar",
                previousUserId, tenantId);
            return;
        }

        client.UserId = newUserId;
        client.FechaActualizacion = DateTime.UtcNow;
        await _clientRepo.UpdateAsync(client, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        _logger.LogInformation("[CRM] Cliente reasignado de BSUID {Prev} a {New} (tenant {Tenant})",
            previousUserId, newUserId, tenantId);
    }

    /// <summary>
    /// Almacena el teléfono compartido por el usuario (respuesta al botón request_contact_info;
    /// webhook type=="contacts", origin=="contact_request"). Complementa al client con su número.
    /// </summary>
    public async Task StoreSharedPhoneAsync(Guid tenantId, string userId, string phone, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(phone))
            return;

        var client = await _clientRepo.GetByUserIdAsync(userId, tenantId, ct);
        if (client == null)
        {
            _logger.LogInformation("[CRM] Teléfono compartido por client {UserId} no existente (tenant {Tenant}): se omite",
                userId, tenantId);
            return;
        }

        if (client.WhatsApp == phone)
            return;

        client.WhatsApp = phone;
        client.FechaActualizacion = DateTime.UtcNow;
        await _clientRepo.UpdateAsync(client, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        _logger.LogInformation("[CRM] Teléfono {Phone} guardado para client {UserId} (tenant {Tenant})",
            phone, userId, tenantId);
    }

    private static string BuildBlock(Client client, List<Appointment> appointments, int totalCitas, DateTime now)
    {
        var cult = CultureInfo.GetCultureInfo("es-ES");

        // "Servicios ya realizados": una cita cuenta como realizada si está marcada 'completed'
        // o si estaba 'confirmed' y su fecha ya pasó (asistida). Sin esto, y como nada en el
        // sistema marca 'completed', el historial siempre aparecería vacío.
        var servicios = appointments
            .Where(a => a.Estado == "completed" || (a.Estado == "confirmed" && a.FechaFin < now))
            .Select(a => a.ServiceType?.Nombre)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .ToList();

        var notas = string.IsNullOrWhiteSpace(client.Notas) ? "—" : client.Notas;
        var tags = string.IsNullOrWhiteSpace(client.Tags) ? "—" : client.Tags;

        string ultimaCita = "—";
        var ultimaCompletada = appointments
            .Where(a => a.Estado == "completed" || (a.Estado == "confirmed" && a.FechaFin < now))
            .OrderByDescending(a => a.FechaInicio)
            .FirstOrDefault();
        if (ultimaCompletada != null)
            ultimaCita = $"{ultimaCompletada.ServiceType?.Nombre} el {ultimaCompletada.FechaInicio:dd 'de' MMMM 'de' yyyy} '{ultimaCompletada.FechaInicio:hh\\:mm}'".Replace("'", "");

        string proximaCita = client.ProximaCita.HasValue
            ? $"{client.ProximaCita.Value.ToString("dddd, dd 'de' MMMM 'de' yyyy' a las 'HH\\:mm", cult)}"
            : "No tiene citas programadas próximas.";

        string serviciosTexto = servicios.Count == 0
            ? "Ninguno todavía."
            : string.Join(", ", servicios.Take(10));

        return @"

CONTEXTO DEL CLIENTE (CRM — memoria operativa, úsalo para personalizar la atención):
- Identidad: " + (string.IsNullOrWhiteSpace(client.Nombre) ? "(sin nombre registrado)" : client.Nombre) + @" (" + client.PartnerId + @")
- Estado del cliente: " + ClientStateCalculator.TraducirEstado(client.Estado) + @"
- Etiquetas: " + tags + @"
- Notas: " + notas + @"
- Total de citas en el historial: " + totalCitas + @"
- Servicios ya realizados: " + serviciosTexto + @"
- Última cita completada: " + ultimaCita + @"
- Próxima cita: " + proximaCita + @"
Usa esto para saludar por nombre si lo conoces, reconocer su historial y sugerirle servicios acordes. No inventes datos que no estén acá.";
    }
}
