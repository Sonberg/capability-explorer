using System.Net.Http;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Dapper;
using Shop.Data;
using Shop.Stubs;

namespace Shop.Infrastructure;

public class AuditLogsRepository(ShopDb db)
{
    public void Append(AuditLog entry) => db.AuditLogs.Add(entry);
}

public class ReviewsRepository(Supabase.Client client)
{
    public Task<Review?> Get() => client.From<Review>().Filter("id", "eq", 1).Single();

    // The contract lets only methods read or write data, so an access inside a property has no edge.
    public object Pending => client.From<Review>();

    public Task<Review?> Add(Review review) => client.From<Review>().Insert(review);
}

public static class BusFactory
{
    public static ServiceBusClient Create(IConfig config) => new(config["ServiceBus:Connection"]!);
}

public class Outbox(ServiceBusClient client)
{
    public object Flush() => client.CreateSender("orders");
}

public class PaymentsClient(HttpClient http)
{
    public Task<HttpResponseMessage> Status() => http.GetAsync("/v1/status");
}

public class ReportJob
{
    public void Run(object connection) => connection.Execute("insert into dbo.Reports (Id) values (1)");
}
