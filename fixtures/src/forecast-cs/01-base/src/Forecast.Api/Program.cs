using Azure.Messaging.ServiceBus;
using Forecast.Api.Clients;
using Forecast.Api.Data;
using Forecast.Api.Endpoints;
using Forecast.Api.Forecasting;
using Forecast.Api.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ForecastDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Forecasts")));
builder.Services.AddScoped(typeof(Repository<>));
builder.Services.AddHttpClient<SalesApiClient>(client =>
    client.BaseAddress = new Uri(builder.Configuration["SalesApi:BaseUrl"]!));
builder.Services.AddKeyedScoped<IForecastStrategy, MovingAverageStrategy>("moving-average");
builder.Services.AddKeyedScoped<IForecastStrategy, ExponentialSmoothingStrategy>("exp-smoothing");
builder.Services.AddSingleton(_ => new ServiceBusClient(builder.Configuration["ServiceBus:ConnectionString"]));
builder.Services.AddHostedService<StatsUpdatedConsumer>();

var app = builder.Build();

app.MapGroup("/api/forecasts").MapForecastEndpoints();

app.Run();
