using Microsoft.EntityFrameworkCore;

namespace Shop.Data;

public class AuditLog { public int Id { get; set; } }

public class Order { public int Id { get; set; } }

public class ShopDb : DbContext
{
    public DbSet<AuditLog> AuditLogs { get; } = new();

    public DbSet<Order> Orders { get; } = new();

    public void OnModelCreating(ModelBuilder model)
    {
        model.Entity<AuditLog>().ToContainer("audit-logs");
        model.Entity<Order>().ToTable("Orders");
    }
}
