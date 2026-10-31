using Forecast.Api.Clients;
using Forecast.Api.Data;
using Forecast.Api.Forecasting;
using Forecast.Api.Generated;
using Microsoft.AspNetCore.Mvc;

namespace Forecast.Api.Endpoints;

public static class ForecastEndpoints
{
    private const int HistoryWeeks = 26;

    public static RouteGroupBuilder MapForecastEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{articleId:int}", GetForecast);
        group.MapGet("/strategies", () => new[] { "moving-average", "exp-smoothing" });
        return group;
    }

    public static async Task<ForecastResponse> GetForecast(
        int articleId,
        [FromQuery] string? strategy,
        SalesApiClient sales,
        Repository<WeeklyHistory> history,
        Repository<ForecastEntry> forecasts,
        IServiceProvider services,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var name = strategy ?? configuration["Forecast:DefaultStrategy"] ?? "moving-average";
        var rows = await history.ListAsync(h => h.ArticleId == articleId, cancellationToken);
        var series = new WeeklySeries(rows.OrderBy(r => r.WeekStart).Select(r => r.Units).ToList());

        var forecast = StrategyLoader.Resolve(services, name).Forecast(series.Quantities);

        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var current = await sales.GetWeeklyAverageAsync(articleId.ToString(System.Globalization.CultureInfo.InvariantCulture), to.AddDays(-7 * HistoryWeeks), to, cancellationToken);

        await forecasts.AddAsync(new ForecastEntry { ArticleId = articleId, Strategy = name, PerWeek = forecast, TotalUnits = series.TotalUnits() }, cancellationToken);
        return new ForecastResponse(articleId, name, forecast, current);
    }
}
