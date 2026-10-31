namespace Forecast.Api.Forecasting;

public interface IForecastStrategy
{
    double Forecast(IReadOnlyList<int> weeklyUnits);
}
