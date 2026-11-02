using System;
using Shop.Application;
using Shop.Archive;
using Shop.Data;
using Shop.Infrastructure;
using Shop.Stubs;

namespace Shop.Host;

public static class Program
{
    public static void Register(IConfig config)
    {
        Services.AddDbContext<ShopDb>(_ => config.GetConnectionString("Shop"));
        Services.AddDbContext<ArchiveDb>(_ => config.GetConnectionString("Archive"));
        Services.AddHttpClient<PaymentsClient>(c => c.BaseAddress = new Uri(config["Payments:Url"]!));
        Services.AddHttpClient<RefundsClient>(c => c.BaseAddress = new Uri(config["Payments:Url"]!));
        _ = config.GetConnectionString("Reports");
    }
}
