// Minimal stand-ins for EF Core, Service Bus, Dapper and DI so the example needs no package restore.
namespace Microsoft.EntityFrameworkCore
{
    public class DbContext { }
    public class DbSet<T> where T : class { public void Add(T item) { } }
    public class ModelBuilder { public EntityTypeBuilder<T> Entity<T>() where T : class => new(); }
    public class EntityTypeBuilder<T> where T : class
    {
        public EntityTypeBuilder<T> ToTable(string name) => this;
        public EntityTypeBuilder<T> ToContainer(string name) => this;
    }
}

namespace Azure.Messaging.ServiceBus
{
    public class ServiceBusSender { }
    public class ServiceBusClient
    {
        public ServiceBusClient(string connection) { }
        public ServiceBusSender CreateSender(string queue) => new();
    }
}

namespace Dapper
{
    public static class SqlMapper
    {
        public static void Execute(this object connection, string sql) { }
        public static void Query<T>(this object connection, string sql) { }
    }
}

namespace Shop.Stubs
{
    public interface IConfig
    {
        string? this[string key] { get; }
        string? GetConnectionString(string name);
    }

    public static class Services
    {
        public static void AddDbContext<T>(System.Action<object> options) { }
        public static void AddHttpClient<T>(System.Action<System.Net.Http.HttpClient> configure) { }
    }
}
