using Microsoft.EntityFrameworkCore;
using Sales.Domain;

namespace Sales.Api.Data;

public interface ISalesRepository
{
    Task<IReadOnlyList<SaleRow>> GetSalesAsync(string articleId, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

public sealed class SalesRepository(SalesDbContext db) : ISalesRepository
{
    public async Task<IReadOnlyList<SaleRow>> GetSalesAsync(string articleId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        return await db.Sales
            .Where(s => s.ArticleId == articleId && s.SoldOn >= from && s.SoldOn <= to)
            .Select(s => new SaleRow(s.ArticleId, s.SoldOn, s.Quantity, s.IsReturn))
            .ToListAsync(cancellationToken);
    }
}
