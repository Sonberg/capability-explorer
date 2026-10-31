using Azure.Messaging.ServiceBus;
using Sales.Worker.Data;
using Sales.Worker.Jobs;
using Sales.Worker.Messaging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(_ => new ServiceBusClient(builder.Configuration["ServiceBus:ConnectionString"]));
builder.Services.AddSingleton<IStatsRepository>(_ => new StatsRepository(builder.Configuration.GetConnectionString("Sales")!));
builder.Services.AddSingleton<StatsPublisher>();
builder.Services.AddHostedService<NightlyStatsJob>();

builder.Build().Run();
