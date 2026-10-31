using System.ComponentModel.DataAnnotations.Schema;

namespace Forecast.Api.Data;

[Table("weekly_history", Schema = "forecast")]
public sealed class WeeklyHistory
{
    public long Id { get; set; }
    public int ArticleId { get; set; }
    public DateOnly WeekStart { get; set; }
    public int Units { get; set; }
}
