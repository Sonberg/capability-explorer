namespace Forecast.Api.Forecasting;

public sealed partial class WeeklySeries(IReadOnlyList<int> quantities)
{
    public IReadOnlyList<int> Quantities { get; } = quantities;
}
