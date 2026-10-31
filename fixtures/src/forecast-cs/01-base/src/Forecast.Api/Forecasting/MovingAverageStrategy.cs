namespace Forecast.Api.Forecasting;

public sealed class MovingAverageStrategy : IForecastStrategy
{
    private const int Window = 4;

    public double Forecast(IReadOnlyList<int> weeklyUnits)
    {
        return weeklyUnits.TakeLast(Window).Average();
    }
}
