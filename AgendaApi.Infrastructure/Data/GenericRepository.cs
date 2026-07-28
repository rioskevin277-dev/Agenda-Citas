using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace AgendaApi.Infrastructure.Data;

/// <summary>
/// Repositorio genérico reutilizando el mismo patrón de AdamApi.
/// </summary>
public class GenericRepository<T> where T : class
{
    protected readonly AgendaDbContext _context;
    protected readonly DbSet<T> _dbSet;

    public GenericRepository(AgendaDbContext context)
    {
        _context = context;
        _dbSet = context.Set<T>();
    }

    public async Task<T?> GetByIdAsync<TKey>(TKey id) where TKey : notnull
    {
        return await _dbSet.FindAsync(id);
    }

    public async Task<List<T>> GetAllAsync()
    {
        return await _dbSet.ToListAsync();
    }

    public async Task<List<T>> GetByFilterAsync(Expression<Func<T, bool>> filter)
    {
        return await _dbSet.Where(filter).ToListAsync();
    }

    public async Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> filter)
    {
        return await _dbSet.FirstOrDefaultAsync(filter);
    }

    public async Task<T> CreateAsync(T entity)
    {
        await _dbSet.AddAsync(entity);
        return entity;
    }

    public Task UpdateAsync(T entity)
    {
        _context.Entry(entity).State = EntityState.Modified;
        return Task.CompletedTask;
    }

    public async Task DeleteAsync<TKey>(TKey id) where TKey : notnull
    {
        var entity = await _dbSet.FindAsync(id)
            ?? throw new InvalidOperationException($"{typeof(T).Name} no encontrado");
        _dbSet.Remove(entity);
    }
}
