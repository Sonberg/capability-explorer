using System.Globalization;
using System.Net.Http.Json;

namespace Forecast.Api.Clients;

public sealed record WeeklySalesDto(string ArticleId, decimal AveragePerWeek);

public sealed class SalesApiClient(HttpClient http)
{
    public async Task<decimal> GetWeeklyAverageAsync(string articleId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var path = $"api/articles/{Uri.EscapeDataString(articleId)}/weekly-sales?from={from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&to={to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        var response = await http.GetFromJsonAsync<WeeklySalesDto>(path, cancellationToken);
        return response?.AveragePerWeek ?? 0m;
    }
}
