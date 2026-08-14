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
    /// Compila el contexto CRM del cliente para la IA. Si el cliente no existe aún en la
    /// BD, lo crea (es su primer contacto) con estado 'nuevo' y devuelve el bloque de
    /// contexto base. Si existe, actualiza UltimaInteraccion y recalcula estado/proxima cita.
    /// </summary>
    public async Task<string> BuildClientContextAsync(
        Guid tenantId,
        string whatsapp,
        CancellationToken ct = default)
    {
        var client = await _clientRepo.GetByWhatsAppAsync(whatsapp, tenantId, ct);

        if (client == null)
        {
            // Primer contacto: se crea el cliente con estado 'nuevo' para que quede en el CRM.
            var nuevo = new Client
            {
                IdClient = Guid.NewGuid(),
                IdTenant = tenantId,
                WhatsApp = whatsapp,
                Estado = "nuevo",
                Activo = true,
                UltimaInteraccion = DateTime.UtcNow
            };
            await _clientRepo.CreateAsync(nuevo, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            _logger.LogInformation("[CRM] Cliente {Whatsapp} creado (nuevo) en tenant {Tenant}", whatsapp, tenantId);
            return BuildBlock(nuevo, new List<Appointment>(), 0);
        }

        // Actualizar la interacción del cliente cada vez que escribe.
        client.UltimaInteraccion = DateTime.UtcNow;
        client.FechaActualizacion = DateTime.UtcNow;

        // Historial de citas del cliente para derivar contexto.
        var appointments = await _appointmentRepo.GetByClientIdAsync(client.IdClient, ct);

        // Recalcular estado del cliente y próxima cita a partir de su historial
        // (helper compartido del CRM, fuente única de verdad).
        ClientStateCalculator.ApplyDerivedState(client, appointments, DateTime.UtcNow);

        await _clientRepo.UpdateAsync(client, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return BuildBlock(client, appointments, appointments.Count);
    }

    private static string BuildBlock(Client client, List<Appointment> appointments, int totalCitas)
    {
        var cult = CultureInfo.GetCultureInfo("es-ES");

        var servicios = appointments
            .Where(a => a.Estado == "completed")
            .Select(a => a.ServiceType?.Nombre)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .ToList();

        var notas = string.IsNullOrWhiteSpace(client.Notas) ? "—" : client.Notas;
        var tags = string.IsNullOrWhiteSpace(client.Tags) ? "—" : client.Tags;

        string ultimaCita = "—";
        var ultimaCompletada = appointments
            .Where(a => a.Estado == "completed")
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
- Identidad: " + (string.IsNullOrWhiteSpace(client.Nombre) ? "(sin nombre registrado)" : client.Nombre) + @" (" + client.WhatsApp + @")
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
