using AgendaApi.Domain.Entities;

namespace AgendaApi.Domain.Ports;

/// <summary>
/// Puerto para el repositorio de tenants.
/// </summary>
public interface ITenantRepository
{
    Task<Tenant?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Tenant?> GetByPhoneNumberIdAsync(string phoneNumberId, CancellationToken ct = default);
    Task<Tenant> CreateAsync(Tenant tenant, CancellationToken ct = default);
    Task UpdateAsync(Tenant tenant, CancellationToken ct = default);
    Task<List<Tenant>> GetAllActiveAsync(CancellationToken ct = default);
}

/// <summary>
/// Puerto para el repositorio de conexiones de calendario.
/// </summary>
public interface ICalendarConnectionRepository
{
    Task<CalendarConnection?> GetByTenantIdAsync(Guid tenantId, CancellationToken ct = default);
    Task<CalendarConnection?> GetByChannelIdAsync(string channelId, CancellationToken ct = default);

    /// <summary>
    /// Devuelve todas las conexiones activas. Se usa para renovar suscripciones webhook
    /// (crear las que no tienen canal, renovar las que están por expirar).
    /// </summary>
    Task<List<CalendarConnection>> GetAllActiveAsync(CancellationToken ct = default);
    Task<CalendarConnection> CreateAsync(CalendarConnection connection, CancellationToken ct = default);
    Task UpdateAsync(CalendarConnection connection, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Puerto para el repositorio de tipos de servicio.
/// </summary>
public interface IServiceTypeRepository
{
    Task<ServiceType?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<ServiceType>> GetByTenantIdAsync(Guid tenantId, CancellationToken ct = default);
    Task<ServiceType> CreateAsync(ServiceType serviceType, CancellationToken ct = default);
    Task UpdateAsync(ServiceType serviceType, CancellationToken ct = default);
}

/// <summary>
/// Puerto para el repositorio de profesionales.
/// </summary>
public interface IProfessionalRepository
{
    Task<Professional?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<Professional>> GetActiveByTenantIdAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Busca un profesional activo por nombre del tenant (para el flujo AI).</summary>
    Task<Professional?> GetActiveByTenantAndNameAsync(Guid tenantId, string nombre, CancellationToken ct = default);

    Task<Professional> CreateAsync(Professional professional, CancellationToken ct = default);
    Task UpdateAsync(Professional professional, CancellationToken ct = default);

    /// <summary>¿El profesional tiene el servicio en su cartera? (ProfessionalService)</summary>
    Task<bool> ProvidesServiceAsync(Guid professionalId, Guid serviceTypeId, CancellationToken ct = default);
    Task<ProfessionalService> AddServiceAsync(ProfessionalService ps, CancellationToken ct = default);
}

/// <summary>
/// Puerto para el repositorio de reglas de disponibilidad.
/// </summary>
public interface IAvailabilityRepository
{
    /// <summary>Reglas del negocio (IdProfessional == null), las que aplican a todo el mundo.</summary>
    Task<List<AvailabilityRule>> GetByTenantIdAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Reglas personales de un profesional (IdProfessional == professionalId).</summary>
    Task<List<AvailabilityRule>> GetByTenantAndProfessionalAsync(Guid tenantId, Guid professionalId, CancellationToken ct = default);

    /// <summary>Excepciones del negocio (IdProfessional == null).</summary>
    Task<List<AvailabilityException>> GetExceptionsByDateRangeAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>Excepciones personales de un profesional (IdProfessional == professionalId).</summary>
    Task<List<AvailabilityException>> GetExceptionsByDateRangeForProfessionalAsync(Guid tenantId, DateTime from, DateTime to, Guid professionalId, CancellationToken ct = default);

    Task<AvailabilityRule> CreateRuleAsync(AvailabilityRule rule, CancellationToken ct = default);
    Task<AvailabilityException> CreateExceptionAsync(AvailabilityException exception, CancellationToken ct = default);
    Task DeleteRuleAsync(Guid id, CancellationToken ct = default);
    Task DeleteExceptionAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Puerto para el repositorio de citas.
/// </summary>
public interface IAppointmentRepository
{
    Task<Appointment?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<Appointment>> GetByTenantIdAsync(Guid tenantId, DateTime? from = null, DateTime? to = null, CancellationToken ct = default);
    Task<List<Appointment>> GetByClientIdAsync(Guid clientId, CancellationToken ct = default);
    Task<List<Appointment>> GetByDateRangeAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>
    /// Citas que ocupan el canal de un profesional: sus propias citas (IdProfessional == professionalId)
    /// más las citas legadas sin profesional (IdProfessional == null), que por compatibilidad
    /// bloquean a cualquiera.
    /// </summary>
    Task<List<Appointment>> GetByDateRangeForProfessionalAsync(Guid tenantId, DateTime from, DateTime to, Guid professionalId, CancellationToken ct = default);
    Task<Appointment> CreateAsync(Appointment appointment, CancellationToken ct = default);
    Task UpdateAsync(Appointment appointment, CancellationToken ct = default);
    /// <summary>
    /// Citas pendientes/confirmadas futuras (FechaInicio > now) candidatas a recordatorio.
    /// La ventana de cada etapa se calcula por tenant en el use case (multi-etapa 24h/2h).
    /// </summary>
    Task<List<Appointment>> GetReminderCandidatesAsync(DateTime now, CancellationToken ct = default);
    Task<Appointment?> GetByExternalEventIdAsync(string externalEventId, CancellationToken ct = default);

    /// <summary>Citas futuras no canceladas sin evento externo (ExternalEventId == null),
    /// para que el job de reparación lo recreé en el calendario.</summary>
    Task<List<Appointment>> GetMissingExternalEventsAsync(CancellationToken ct = default);
}

/// <summary>
/// Puerto para el repositorio de clientes.
/// </summary>
public interface IClientRepository
{
    Task<Client?> GetByWhatsAppAsync(string whatsapp, Guid tenantId, CancellationToken ct = default);
    Task<Client?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Client> CreateAsync(Client client, CancellationToken ct = default);
    Task UpdateAsync(Client client, CancellationToken ct = default);
}

/// <summary>
/// Puerto para el registro de recordatorios por cita y etapa (dedup + estados + reintentos).
/// </summary>
public interface IReminderLogRepository
{
    Task<List<ReminderLog>> GetByAppointmentIdsAsync(IEnumerable<Guid> appointmentIds, CancellationToken ct = default);
    Task<ReminderLog?> GetByWamIdAsync(string wamId, CancellationToken ct = default);
    Task AddAsync(ReminderLog log, CancellationToken ct = default);
    Task UpdateAsync(ReminderLog log, CancellationToken ct = default);
}
