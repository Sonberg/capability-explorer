using Microsoft.EntityFrameworkCore;

namespace Sales.Api.Data;

public sealed class SalesDbContext(DbContextOptions<SalesDbContext> options) : DbContext(options)
{
    public DbSet<SaleEntity> Sales => Set<SaleEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SaleEntity>(entity =>
        {
            entity.ToTable("Sales", "dbo");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Quantity).HasPrecision(18, 3);
        });
    }
}
