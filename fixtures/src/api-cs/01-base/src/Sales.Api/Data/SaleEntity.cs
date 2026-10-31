namespace Sales.Api.Data;

public sealed class SaleEntity
{
    public long Id { get; set; }
    public required string ArticleId { get; set; }
    public DateOnly SoldOn { get; set; }
    public decimal? Quantity { get; set; }
    public bool IsReturn { get; set; }
}
