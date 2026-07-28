namespace AgendaApi.Domain.Ports;

/// <summary>
/// Puerto para Unit of Work (transacciones).
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
