namespace Sales.Domain;

public sealed record SaleRow(string ArticleId, DateOnly SoldOn, decimal? Quantity, bool IsReturn);
