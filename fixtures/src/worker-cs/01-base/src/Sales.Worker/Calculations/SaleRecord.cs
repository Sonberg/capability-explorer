namespace Sales.Worker.Calculations;

public sealed record SaleRecord(string ArticleId, DateTime SoldOn, decimal? Quantity);
