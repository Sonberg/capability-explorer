using System.Net.Http;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Dapper;
using Shop.Data;

namespace Shop.Application;

public class GetAuditLogsQuery(ShopDb db)
{
    public object Run() => db.AuditLogs;
}

public class OrderNotifier(ServiceBusClient client)
{
    public object Send() => client.CreateSender("orders");
}

public class RefundsClient(HttpClient http)
{
    public Task<HttpResponseMessage> Status() => http.GetAsync("/v1/status");
}

public class WeeklyReport
{
    public void Run(object connection) => connection.Query<int>("select * from dbo.Reports");
}
