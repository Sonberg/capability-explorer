using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Forecast.Api.Data;

public sealed class Repository<T>(ForecastDbContext db)
    where T : class
{
    public Task<List<T>> ListAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken) =>
        db.Set<T>().Where(predicate).ToListAsync(cancellationToken);

    public async Task AddAsync(T entity, CancellationToken cancellationToken)
    {
        db.Set<T>().Add(entity);
        await db.SaveChangesAsync(cancellationToken);
    }
}
