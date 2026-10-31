namespace Forecast.Api.Forecasting;

public static class StrategyLoader
{
    private const string PluginPrefix = "plugin:";

    public static IForecastStrategy Resolve(IServiceProvider services, string name)
    {
        if (name.StartsWith(PluginPrefix, StringComparison.Ordinal))
        {
            var type = Type.GetType(name[PluginPrefix.Length..], throwOnError: true)!;
            return (IForecastStrategy)Activator.CreateInstance(type)!;
        }

        return services.GetRequiredKeyedService<IForecastStrategy>(name);
    }
}
