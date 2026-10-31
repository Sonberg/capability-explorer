using Sales.Domain;

namespace Sales.Api.Calculations;

public interface IWeeklySalesCalculator
{
    decimal CalendarWeekAverage(IReadOnlyList<SaleRow> rows, DateOnly from, DateOnly to);
}
