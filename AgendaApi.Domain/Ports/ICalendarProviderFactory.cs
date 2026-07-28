namespace AgendaApi.Domain.Ports;

/// <summary>
/// Puerto para la fábrica de proveedores de calendario.
/// Resuelve qué adaptador de calendario usar según el tenant.
/// </summary>
public interface ICalendarProviderFactory
{
    /// <summary>
    /// Obtiene el proveedor de calendario configurado para un tenant.
    /// </summary>
    Task<ICalendarProvider?> GetProviderAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Obtiene un proveedor por nombre.
    /// </summary>
    ICalendarProvider? GetProviderByName(string providerName);
}
