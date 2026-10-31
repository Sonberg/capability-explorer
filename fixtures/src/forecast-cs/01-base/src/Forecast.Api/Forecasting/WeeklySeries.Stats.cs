namespace Forecast.Api.Forecasting;

public sealed partial class WeeklySeries
{
    public int TotalUnits()
    {
        var total = 0;
        foreach (var units in Quantities)
        {
            total += units;
        }

        return total;
    }
}
