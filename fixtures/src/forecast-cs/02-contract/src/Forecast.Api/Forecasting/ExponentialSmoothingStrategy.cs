namespace Forecast.Api.Forecasting;

public sealed class ExponentialSmoothingStrategy : IForecastStrategy
{
    private const double Alpha = 0.5;

    public double Forecast(IReadOnlyList<int> weeklyUnits)
    {
        if (weeklyUnits.Count == 0)
        {
            return double.NaN;
        }

        double level = weeklyUnits[0];
        foreach (var units in weeklyUnits.Skip(1))
        {
            level = Alpha * units + (1 - Alpha) * level;
        }

        return Math.Round(level, 1);
    }
}
