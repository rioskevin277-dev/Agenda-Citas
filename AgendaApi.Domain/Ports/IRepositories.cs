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
/// Puerto para el repositorio de reglas de disponibilidad.
/// </summary>
public interface IAvailabilityRepository
{
    Task<List<AvailabilityRule>> GetByTenantIdAsync(Guid tenantId, CancellationToken ct = default);
    Task<List<AvailabilityException>> GetExceptionsByDateRangeAsync(Guid tenantId, DateTime from, DateTime to, CancellationToken ct = default);
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
    Task<Appointment> CreateAsync(Appointment appointment, CancellationToken ct = default);
    Task UpdateAsync(Appointment appointment, CancellationToken ct = default);
    Task<List<Appointment>> GetPendingRemindersAsync(CancellationToken ct = default);
    Task<Appointment?> GetByExternalEventIdAsync(string externalEventId, CancellationToken ct = default);
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
