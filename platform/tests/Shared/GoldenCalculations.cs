using CapabilityExplorer.Contracts;
using CapabilityExplorer.Interpretation;

namespace CapabilityExplorer.Tests;

/// <summary>Calculation sides built from golden artifacts exactly as the explorer builds them.</summary>
public static class GoldenCalculations
{
    public const string ApiCalendar = "cs|api-cs|Sales.Api|M:Sales.Api.Calculations.WeeklySalesCalculator.CalendarWeekAverage(System.Collections.Generic.IReadOnlyList{Sales.Domain.SaleRow},System.DateOnly,System.DateOnly)";
    public const string WorkerActive = "cs|worker-cs|Sales.Worker|M:Sales.Worker.Calculations.ArticleStatsCalculator.AveragePerActiveWeek(System.Collections.Generic.IReadOnlyCollection{Sales.Worker.Calculations.SaleRecord})";
    public const string Smoothing = "cs|forecast-cs|Forecast.Api|M:Forecast.Api.Forecasting.ExponentialSmoothingStrategy.Forecast(System.Collections.Generic.IReadOnlyList{System.Int32})";

    public static CalculationSide Side(string file, string key)
    {
        var artifact = ArtifactValidator.Validate(Fixtures.Golden(file)).Artifact!;
        var calc = artifact.Calculations.Single(c => c.SymbolKey == key);
        var name = artifact.Nodes.Single(n => n.SymbolKey == key).Name;
        return new CalculationSide(key, name, artifact.Header.RepositoryId, artifact.Header.CommitSha, artifact.Header.Language, calc.Raw);
    }

    /// <summary>Latest api-cs calendar-week average vs the worker's active-week average.</summary>
    public static CalculationComparison CalendarVsActive() => CalculationComparer.Compare(
        Side("api-cs/v3-filter/Sales.Api.jsonl", ApiCalendar),
        Side("worker-cs/v2-broken/Sales.Worker.jsonl", WorkerActive));
}
