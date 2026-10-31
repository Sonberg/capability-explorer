namespace Forecast.Api.Data;

public sealed class ForecastEntry
{
    public long Id { get; set; }
    public int ArticleId { get; set; }
    public required string Strategy { get; set; }
    public double PerWeek { get; set; }
    public int TotalUnits { get; set; }
}
