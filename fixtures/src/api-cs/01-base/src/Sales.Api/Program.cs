using Microsoft.EntityFrameworkCore;
using Sales.Api.Calculations;
using Sales.Api.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddDbContext<SalesDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Sales")));
builder.Services.AddScoped<ISalesRepository, SalesRepository>();
builder.Services.AddScoped<IWeeklySalesCalculator, WeeklySalesCalculator>();

var app = builder.Build();

app.MapControllers();
app.MapGet("/api/health", () => Results.Ok());

app.Run();
