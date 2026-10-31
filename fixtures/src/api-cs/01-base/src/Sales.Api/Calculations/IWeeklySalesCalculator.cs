using Sales.Domain;

namespace Sales.Api.Calculations;

public interface IWeeklySalesCalculator
{
    decimal AveragePerCalendarWeek(IReadOnlyList<SaleRow> rows, DateOnly from, DateOnly to);
}
