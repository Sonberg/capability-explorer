using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace Sales.Worker.Messaging;

public sealed class StatsPublisher(ServiceBusClient client)
{
    private const string TopicName = "article-stats-updated";

    public async Task PublishAsync(ArticleStatsUpdated message, CancellationToken cancellationToken)
    {
        await using var sender = client.CreateSender(TopicName);
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        await sender.SendMessageAsync(new ServiceBusMessage(body) { Subject = nameof(ArticleStatsUpdated) }, cancellationToken);
    }
}
