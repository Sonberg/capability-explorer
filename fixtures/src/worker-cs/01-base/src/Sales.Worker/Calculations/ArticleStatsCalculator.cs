using System.Globalization;

namespace Sales.Worker.Calculations;

public static class ArticleStatsCalculator
{
    public static decimal AveragePerActiveWeek(IReadOnlyCollection<SaleRecord> rows)
    {
        var withQuantity = rows.Where(r => r.Quantity.HasValue).ToList();
        var activeWeeks = withQuantity
            .Select(r => (ISOWeek.GetYear(r.SoldOn), ISOWeek.GetWeekOfYear(r.SoldOn)))
            .Distinct()
            .Count();

        if (activeWeeks == 0)
        {
            return 0m;
        }

        var total = withQuantity.Sum(r => r.Quantity!.Value);
        return Math.Round(total / activeWeeks, 2, MidpointRounding.AwayFromZero);
    }
}
