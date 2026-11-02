using Microsoft.EntityFrameworkCore;

namespace Shop.Archive;

public class ArchivedLog { public int Id { get; set; } }

// A different store that happens to use the same container name as ShopDb.
public class ArchiveDb : DbContext
{
    public DbSet<ArchivedLog> Logs { get; } = new();

    public void OnModelCreating(ModelBuilder model) => model.Entity<ArchivedLog>().ToContainer("audit-logs");

    public object Read() => Logs;
}
