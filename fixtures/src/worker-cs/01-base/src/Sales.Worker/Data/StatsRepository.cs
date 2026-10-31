using Dapper;
using Microsoft.Data.SqlClient;
using Sales.Worker.Calculations;

namespace Sales.Worker.Data;

public interface IStatsRepository
{
    Task<IReadOnlyList<SaleRecord>> GetSalesSinceAsync(DateTime since);
    Task SaveAverageAsync(string articleId, decimal averagePerWeek);
}

public sealed class StatsRepository(string connectionString) : IStatsRepository
{
    private const string SelectSales =
        "SELECT ArticleId, SoldOn, Quantity FROM dbo.Sales WHERE SoldOn >= @Since";

    private const string UpdateStats =
        "UPDATE dbo.ArticleStats SET AveragePerWeek = @AveragePerWeek, UpdatedAt = SYSUTCDATETIME() WHERE ArticleId = @ArticleId";

    public async Task<IReadOnlyList<SaleRecord>> GetSalesSinceAsync(DateTime since)
    {
        await using var connection = new SqlConnection(connectionString);
        var rows = await connection.QueryAsync<SaleRecord>(SelectSales, new { Since = since });
        return rows.ToList();
    }

    public async Task SaveAverageAsync(string articleId, decimal averagePerWeek)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.ExecuteAsync(UpdateStats, new { ArticleId = articleId, AveragePerWeek = averagePerWeek });
    }
}
