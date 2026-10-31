namespace Sales.Worker.Messaging;

public sealed record ArticleStatsUpdated(string ArticleId, decimal AveragePerWeek, DateTime ComputedAt);
