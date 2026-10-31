using Azure.Messaging.ServiceBus;

namespace Forecast.Api.Messaging;

public sealed record ArticleStatsUpdatedV1(string ArticleId, decimal AveragePerWeek);

public sealed partial class StatsUpdatedConsumer(ServiceBusClient client, ILogger<StatsUpdatedConsumer> logger) : BackgroundService
{
    private const string TopicName = "article-stats-updated";
    private const string SubscriptionName = "forecast";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var processor = client.CreateProcessor(TopicName, SubscriptionName);
        processor.ProcessMessageAsync += HandleAsync;
        processor.ProcessErrorAsync += args =>
        {
            LogFailure(args.Exception);
            return Task.CompletedTask;
        };
        await processor.StartProcessingAsync(stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task HandleAsync(ProcessMessageEventArgs args)
    {
        var message = args.Message.Body.ToObjectFromJson<ArticleStatsUpdatedV1>()!;
        LogReceived(message.ArticleId, message.AveragePerWeek);
        await args.CompleteMessageAsync(args.Message);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stats updated for {ArticleId}: {AveragePerWeek}")]
    private partial void LogReceived(string articleId, decimal averagePerWeek);

    [LoggerMessage(Level = LogLevel.Error, Message = "Service Bus processing failed")]
    private partial void LogFailure(Exception exception);
}
