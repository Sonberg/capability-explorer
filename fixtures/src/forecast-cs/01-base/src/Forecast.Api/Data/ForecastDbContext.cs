using Microsoft.EntityFrameworkCore;

namespace Forecast.Api.Data;

public sealed class ForecastDbContext(DbContextOptions<ForecastDbContext> options) : DbContext(options)
{
    public DbSet<ForecastEntry> Forecasts => Set<ForecastEntry>();

    public DbSet<WeeklyHistory> History => Set<WeeklyHistory>();
}
