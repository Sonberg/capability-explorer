using Sales.Worker.Calculations;
using Sales.Worker.Data;
using Sales.Worker.Messaging;

namespace Sales.Worker.Jobs;

public sealed class NightlyStatsJob(IStatsRepository repository, StatsPublisher publisher) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var since = DateTime.UtcNow.Date.AddDays(-91);
        var sales = await repository.GetSalesSinceAsync(since);

        foreach (var article in sales.GroupBy(s => s.ArticleId))
        {
            var average = ArticleStatsCalculator.AveragePerActiveWeek(article.ToList());
            await repository.SaveAverageAsync(article.Key, average);
            await publisher.PublishAsync(new ArticleStatsUpdated(article.Key, average, DateTime.UtcNow), stoppingToken);
        }
    }
}
