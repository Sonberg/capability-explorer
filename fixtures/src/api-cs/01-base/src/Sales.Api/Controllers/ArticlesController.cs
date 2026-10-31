using Microsoft.AspNetCore.Mvc;
using Sales.Api.Calculations;
using Sales.Api.Data;

namespace Sales.Api.Controllers;

public sealed record WeeklySalesResponse(string ArticleId, DateOnly From, DateOnly To, decimal AveragePerWeek);

[ApiController]
[Route("api/articles")]
public sealed class ArticlesController(ISalesRepository repository, IWeeklySalesCalculator calculator) : ControllerBase
{
    [HttpGet("{articleId}/weekly-sales")]
    public async Task<ActionResult<WeeklySalesResponse>> GetWeeklySales(
        string articleId,
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        CancellationToken cancellationToken)
    {
        var rows = await repository.GetSalesAsync(articleId, from, to, cancellationToken);
        var average = calculator.AveragePerCalendarWeek(rows, from, to);
        return new WeeklySalesResponse(articleId, from, to, average);
    }
}
