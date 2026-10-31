import { Artifact, ir, key } from "../lib.mjs";

const R = "forecast-cs";
const A = "Forecast.Api";
const RUNTIME = "System.Runtime";
const ROUTES = { name: "aspnetcore.routes", version: "1" };
const DI = { name: "dotnet.di", version: "1" };
const HTTP = { name: "dotnet.httpclient", version: "1" };
const EF = { name: "efcore", version: "1" };
const SB = { name: "azure.servicebus", version: "1" };

const cs = (docId) => key.cs(R, A, docId);
const INTS = "System.Collections.Generic.IReadOnlyList{System.Int32}";
const conn = key.unbound(R, A, "conn", "Forecasts");

export const k = {
  proj: key.proj(R, "src/Forecast.Api/Forecast.Api.csproj"),
  service: key.svc(R, A),
  program: cs("M:Program.<Main>$(System.String[])"),
  endpoints: cs("T:Forecast.Api.Endpoints.ForecastEndpoints"),
  mapEndpoints: cs("M:Forecast.Api.Endpoints.ForecastEndpoints.MapForecastEndpoints(Microsoft.AspNetCore.Routing.RouteGroupBuilder)"),
  getForecast: cs(
    "M:Forecast.Api.Endpoints.ForecastEndpoints.GetForecast(System.Int32,System.String,Forecast.Api.Clients.SalesApiClient,Forecast.Api.Data.Repository{Forecast.Api.Data.WeeklyHistory},Forecast.Api.Data.Repository{Forecast.Api.Data.ForecastEntry},System.IServiceProvider,Microsoft.Extensions.Configuration.IConfiguration,System.Threading.CancellationToken)",
  ),
  salesClient: cs("T:Forecast.Api.Clients.SalesApiClient"),
  getWeeklyAverage: cs("M:Forecast.Api.Clients.SalesApiClient.GetWeeklyAverageAsync(System.String,System.DateOnly,System.DateOnly,System.Threading.CancellationToken)"),
  iStrategy: cs("T:Forecast.Api.Forecasting.IForecastStrategy"),
  iForecast: cs(`M:Forecast.Api.Forecasting.IForecastStrategy.Forecast(${INTS})`),
  moving: cs("T:Forecast.Api.Forecasting.MovingAverageStrategy"),
  movingForecast: cs(`M:Forecast.Api.Forecasting.MovingAverageStrategy.Forecast(${INTS})`),
  smoothing: cs("T:Forecast.Api.Forecasting.ExponentialSmoothingStrategy"),
  smoothingForecast: cs(`M:Forecast.Api.Forecasting.ExponentialSmoothingStrategy.Forecast(${INTS})`),
  loader: cs("T:Forecast.Api.Forecasting.StrategyLoader"),
  resolve: cs("M:Forecast.Api.Forecasting.StrategyLoader.Resolve(System.IServiceProvider,System.String)"),
  series: cs("T:Forecast.Api.Forecasting.WeeklySeries"),
  quantities: cs("P:Forecast.Api.Forecasting.WeeklySeries.Quantities"),
  totalUnits: cs("M:Forecast.Api.Forecasting.WeeklySeries.TotalUnits"),
  dbContext: cs("T:Forecast.Api.Data.ForecastDbContext"),
  forecastEntry: cs("T:Forecast.Api.Data.ForecastEntry"),
  weeklyHistory: cs("T:Forecast.Api.Data.WeeklyHistory"),
  repository: cs("T:Forecast.Api.Data.Repository`1"),
  listAsync: cs("M:Forecast.Api.Data.Repository`1.ListAsync(System.Linq.Expressions.Expression{System.Func{`0,System.Boolean}},System.Threading.CancellationToken)"),
  addAsync: cs("M:Forecast.Api.Data.Repository`1.AddAsync(`0,System.Threading.CancellationToken)"),
  consumer: cs("T:Forecast.Api.Messaging.StatsUpdatedConsumer"),
  consumerExecute: cs("M:Forecast.Api.Messaging.StatsUpdatedConsumer.ExecuteAsync(System.Threading.CancellationToken)"),
  consumerHandle: cs("M:Forecast.Api.Messaging.StatsUpdatedConsumer.HandleAsync(Azure.Messaging.ServiceBus.ProcessMessageEventArgs)"),
  message: cs("T:Forecast.Api.Messaging.ArticleStatsUpdatedV1"),
  response: cs("T:Forecast.Api.Generated.ForecastResponse"),
  topic: key.chan("servicebus", key.unbound(R, A, "config", "ServiceBus:ConnectionString"), "topic/article-stats-updated"),
  historyTable: key.sql(conn, "forecast.weekly_history"),
  forecastsTable: key.sql(conn, "public.Forecasts"),
  salesEndpoint: key.http(key.unbound(R, A, "config", "SalesApi:BaseUrl"), "GET", "/api/articles/{}/weekly-sales"),
  takeLast: key.csExt("System.Linq", "M:System.Linq.Enumerable.TakeLast``1(System.Collections.Generic.IEnumerable{``0},System.Int32)"),
  average: key.csExt("System.Linq", "M:System.Linq.Enumerable.Average(System.Collections.Generic.IEnumerable{System.Int32})"),
  roundDouble: key.csExt(RUNTIME, "M:System.Math.Round(System.Double,System.Int32)"),
  activator: key.csExt(RUNTIME, "M:System.Activator.CreateInstance(System.Type)"),
  keyedService: key.csExt(
    "Microsoft.Extensions.DependencyInjection.Abstractions",
    "M:Microsoft.Extensions.DependencyInjection.ServiceProviderKeyedServiceExtensions.GetRequiredKeyedService``1(System.IServiceProvider,System.Object)",
  ),
};

export const prefix = (tag) => (tag === "v1-base" ? "/api/forecasts" : "/api/v2/forecasts");
export const forecastEndpoint = (tag) => key.http(`${R}/${A}`, "GET", `${prefix(tag)}/{}`);
const strategiesEndpoint = (tag) => key.http(`${R}/${A}`, "GET", `${prefix(tag)}/strategies`);

export default function forecastCs(repo, tag) {
  const a = new Artifact(repo, {
    unitPath: "src/Forecast.Api/Forecast.Api.csproj", unitName: A, language: "csharp",
    sourcePrefix: "src/Forecast.Api", sourceExtensions: [".cs"], rules: [ROUTES, DI, HTTP, EF, SB],
  });
  const csproj = "src/Forecast.Api/Forecast.Api.csproj";
  const settings = "src/Forecast.Api/appsettings.json";
  const program = "src/Forecast.Api/Program.cs";
  const ep = "src/Forecast.Api/Endpoints/ForecastEndpoints.cs";
  const client = "src/Forecast.Api/Clients/SalesApiClient.cs";
  const fdir = "src/Forecast.Api/Forecasting";
  const istrat = `${fdir}/IForecastStrategy.cs`;
  const mov = `${fdir}/MovingAverageStrategy.cs`;
  const exp = `${fdir}/ExponentialSmoothingStrategy.cs`;
  const loader = `${fdir}/StrategyLoader.cs`;
  const series = `${fdir}/WeeklySeries.cs`;
  const seriesStats = `${fdir}/WeeklySeries.Stats.cs`;
  const ctx = "src/Forecast.Api/Data/ForecastDbContext.cs";
  const entry = "src/Forecast.Api/Data/ForecastEntry.cs";
  const hist = "src/Forecast.Api/Data/WeeklyHistory.cs";
  const repoFile = "src/Forecast.Api/Data/Repository.cs";
  const consumer = "src/Forecast.Api/Messaging/StatsUpdatedConsumer.cs";
  const gen = "src/Forecast.Api/Generated/ForecastContracts.g.cs";
  const groupCall = `app.MapGroup("${prefix(tag)}")`;
  const alpha = tag === "v1-base" ? 0.3 : 0.5;

  // ---- nodes ----
  a.node({ key: k.proj, kind: "Project", name: A, at: [csproj, '<Project Sdk="Microsoft.NET.Sdk.Web">'] });
  a.node({ key: k.service, kind: "Service", name: A, props: { sdk: "Microsoft.NET.Sdk.Web" }, at: [csproj, '<Project Sdk="Microsoft.NET.Sdk.Web">'] });
  a.node({ key: k.program, kind: "Method", name: "<Main>$", props: { topLevelStatements: true }, at: [program, "var builder = WebApplication.CreateBuilder(args);"] });
  a.node({
    key: forecastEndpoint(tag), kind: "Endpoint", name: `GET ${prefix(tag)}/{articleId:int}`,
    props: { method: "GET", route: `${prefix(tag)}/{articleId:int}`, normalizedRoute: `${prefix(tag)}/{}`, style: "minimal-api", composedFrom: ["MapGroup", "MapGet"] },
    at: [[program, groupCall], [ep, 'group.MapGet("/{articleId:int}", GetForecast)']],
  });
  a.node({
    key: strategiesEndpoint(tag), kind: "Endpoint", name: `GET ${prefix(tag)}/strategies`,
    props: { method: "GET", route: `${prefix(tag)}/strategies`, normalizedRoute: `${prefix(tag)}/strategies`, style: "minimal-api", composedFrom: ["MapGroup", "MapGet"] },
    at: [[program, groupCall], [ep, 'group.MapGet("/strategies"']],
  });
  a.node({ key: k.endpoints, kind: "Type", name: "ForecastEndpoints", props: { typeKind: "class", static: true }, at: [ep, "public static class ForecastEndpoints"] });
  a.node({ key: k.mapEndpoints, kind: "Method", name: "MapForecastEndpoints", props: { extensionMethod: true }, at: [ep, "public static RouteGroupBuilder MapForecastEndpoints(this RouteGroupBuilder group)"] });
  a.node({ key: k.getForecast, kind: "Method", name: "GetForecast", at: [ep, "public static async Task<ForecastResponse> GetForecast("] });
  a.node({ key: k.salesClient, kind: "Type", name: "SalesApiClient", props: { typeKind: "class" }, at: [client, "public sealed class SalesApiClient(HttpClient http)"] });
  a.node({ key: k.getWeeklyAverage, kind: "Method", name: "GetWeeklyAverageAsync", at: [client, "public async Task<decimal> GetWeeklyAverageAsync("] });
  a.node({ key: k.iStrategy, kind: "Type", name: "IForecastStrategy", props: { typeKind: "interface" }, at: [istrat, "public interface IForecastStrategy"] });
  a.node({ key: k.iForecast, kind: "Method", name: "Forecast", at: [istrat, "double Forecast(IReadOnlyList<int> weeklyUnits);"] });
  a.node({ key: k.moving, kind: "Type", name: "MovingAverageStrategy", props: { typeKind: "class" }, at: [mov, "public sealed class MovingAverageStrategy"] });
  a.node({ key: k.movingForecast, kind: "Method", name: "Forecast", at: [mov, "public double Forecast(IReadOnlyList<int> weeklyUnits)"] });
  a.node({ key: k.smoothing, kind: "Type", name: "ExponentialSmoothingStrategy", props: { typeKind: "class" }, at: [exp, "public sealed class ExponentialSmoothingStrategy"] });
  a.node({ key: k.smoothingForecast, kind: "Method", name: "Forecast", at: [exp, "public double Forecast(IReadOnlyList<int> weeklyUnits)"] });
  a.node({ key: k.loader, kind: "Type", name: "StrategyLoader", props: { typeKind: "class", static: true }, at: [loader, "public static class StrategyLoader"] });
  a.node({ key: k.resolve, kind: "Method", name: "Resolve", at: [loader, "public static IForecastStrategy Resolve(IServiceProvider services, string name)"] });
  // Partial class: one symbol, declared in two files.
  a.node({
    key: k.series, kind: "Type", name: "WeeklySeries", props: { typeKind: "class", partial: true },
    at: [[series, "public sealed partial class WeeklySeries(IReadOnlyList<int> quantities)"], [seriesStats, "public sealed partial class WeeklySeries"]],
  });
  a.node({ key: k.quantities, kind: "Property", name: "Quantities", at: [series, "public IReadOnlyList<int> Quantities { get; }"] });
  a.node({ key: k.totalUnits, kind: "Method", name: "TotalUnits", at: [seriesStats, "public int TotalUnits()"] });
  a.node({ key: k.dbContext, kind: "Type", name: "ForecastDbContext", props: { typeKind: "class" }, at: [ctx, "public sealed class ForecastDbContext"] });
  a.node({ key: k.forecastEntry, kind: "Type", name: "ForecastEntry", props: { typeKind: "class" }, at: [entry, "public sealed class ForecastEntry"] });
  a.node({ key: k.weeklyHistory, kind: "Type", name: "WeeklyHistory", props: { typeKind: "class" }, at: [hist, "public sealed class WeeklyHistory"] });
  a.node({ key: k.repository, kind: "Type", name: "Repository", props: { typeKind: "class", genericArity: 1 }, at: [repoFile, "public sealed class Repository<T>(ForecastDbContext db)"] });
  a.node({ key: k.listAsync, kind: "Method", name: "ListAsync", at: [repoFile, "public Task<List<T>> ListAsync("] });
  a.node({ key: k.addAsync, kind: "Method", name: "AddAsync", at: [repoFile, "public async Task AddAsync(T entity, CancellationToken cancellationToken)"] });
  a.node({ key: k.consumer, kind: "Type", name: "StatsUpdatedConsumer", props: { typeKind: "class", partial: true }, at: [consumer, "public sealed partial class StatsUpdatedConsumer"] });
  a.node({ key: k.consumerExecute, kind: "Method", name: "ExecuteAsync", at: [consumer, "protected override async Task ExecuteAsync(CancellationToken stoppingToken)"] });
  a.node({ key: k.consumerHandle, kind: "Method", name: "HandleAsync", at: [consumer, "private async Task HandleAsync(ProcessMessageEventArgs args)"] });
  a.node({ key: k.message, kind: "Type", name: "ArticleStatsUpdatedV1", props: { typeKind: "record", messageContract: true }, at: [consumer, "public sealed record ArticleStatsUpdatedV1"] });
  a.node({
    key: k.response, kind: "Type", name: "ForecastResponse", generated: true,
    props: { typeKind: "record", generator: "contract-gen 2.1" },
    at: [[gen, "// <auto-generated>"], [gen, "public sealed partial record ForecastResponse"]],
  });
  a.node({
    key: k.topic, kind: "Channel", name: "article-stats-updated",
    props: { provider: "azure-servicebus", entityType: "topic", namespaceConfigKey: "ServiceBus:ConnectionString" },
    at: [[consumer, 'private const string TopicName = "article-stats-updated";'], [program, 'builder.Configuration["ServiceBus:ConnectionString"]']],
  });

  // ---- architecture ----
  a.edge({ from: k.service, rel: "EXPOSES", to: forecastEndpoint(tag), derivation: "rule", rule: ROUTES, at: [ep, 'group.MapGet("/{articleId:int}", GetForecast)'] });
  a.edge({ from: k.service, rel: "EXPOSES", to: strategiesEndpoint(tag), derivation: "rule", rule: ROUTES, at: [ep, 'group.MapGet("/strategies"'] });
  a.edge({ from: k.getForecast, rel: "HANDLES", to: forecastEndpoint(tag), derivation: "rule", rule: ROUTES, at: [ep, 'group.MapGet("/{articleId:int}", GetForecast)'] });
  // Lambda handlers are attributed to their nearest named container.
  a.edge({
    from: k.mapEndpoints, rel: "HANDLES", to: strategiesEndpoint(tag), derivation: "rule", rule: ROUTES,
    props: { handler: "lambda" }, at: [ep, '() => new[] { "moving-average", "exp-smoothing" }'],
  });
  a.edge({
    from: k.getWeeklyAverage, rel: "REQUESTS", to: k.salesEndpoint, derivation: "rule", rule: HTTP,
    resolution: "unresolved", reason: "configuration",
    props: {
      method: "GET", normalizedRoute: "/api/articles/{}/weekly-sales", query: ["from", "to"],
      baseUrlConfigKey: "SalesApi:BaseUrl", configuredBaseUrl: "http://sales-api/", configSource: settings,
      relativeToBaseAddress: true, responseType: cs("T:Forecast.Api.Clients.WeeklySalesDto"),
    },
    at: [
      [client, '$"api/articles/{Uri.EscapeDataString(articleId)}/weekly-sales'],
      [client, "http.GetFromJsonAsync<WeeklySalesDto>(path, cancellationToken)"],
      [program, 'client.BaseAddress = new Uri(builder.Configuration["SalesApi:BaseUrl"]!)'],
      [settings, '"BaseUrl": "http://sales-api/"'],
    ],
  });
  a.edge({
    from: k.consumerExecute, rel: "CONSUMES", to: k.topic, derivation: "rule", rule: SB,
    resolution: "unresolved", reason: "configuration",
    props: { subscription: "forecast", contract: k.message, handler: k.consumerHandle },
    at: [[consumer, "client.CreateProcessor(TopicName, SubscriptionName)"], [consumer, "processor.ProcessMessageAsync += HandleAsync;"]],
  });
  a.edge({
    from: k.weeklyHistory, rel: "MAPS_TO", to: k.historyTable, derivation: "rule", rule: EF,
    resolution: "unresolved", reason: "configuration",
    props: { mapping: "attribute", connectionName: "Forecasts", schema: "forecast", table: "weekly_history" },
    at: [[hist, '[Table("weekly_history", Schema = "forecast")]'], [program, 'GetConnectionString("Forecasts")']],
  });
  a.edge({
    from: k.forecastEntry, rel: "MAPS_TO", to: k.forecastsTable, derivation: "rule", rule: EF,
    resolution: "unresolved", reason: "configuration",
    props: { mapping: "convention", convention: "DbSet property name; provider default schema", connectionName: "Forecasts", schema: "public", table: "Forecasts" },
    at: [[ctx, "public DbSet<ForecastEntry> Forecasts => Set<ForecastEntry>();"], [program, 'GetConnectionString("Forecasts")']],
  });
  // Data access through an open generic repository: the table depends on the type argument at the call site.
  a.edge({
    from: k.getForecast, rel: "READS", to: k.historyTable, derivation: "rule", rule: EF,
    resolution: "unresolved", reason: "configuration",
    props: { via: k.listAsync, typeArgument: k.weeklyHistory },
    at: [ep, "history.ListAsync(h => h.ArticleId == articleId, cancellationToken)"],
  });
  a.edge({
    from: k.getForecast, rel: "WRITES", to: k.forecastsTable, derivation: "rule", rule: EF,
    resolution: "unresolved", reason: "configuration",
    props: { via: k.addAsync, typeArgument: k.forecastEntry },
    at: [ep, "forecasts.AddAsync(new ForecastEntry"],
  });

  // ---- DI ----
  a.edge({ from: k.program, rel: "REGISTERS", to: k.repository, derivation: "rule", rule: DI, props: { lifetime: "scoped", openGeneric: true }, at: [program, "builder.Services.AddScoped(typeof(Repository<>))"] });
  a.edge({ from: k.program, rel: "REGISTERS", to: k.salesClient, derivation: "rule", rule: DI, props: { lifetime: "transient", registration: "typedHttpClient" }, at: [program, "builder.Services.AddHttpClient<SalesApiClient>("] });
  a.edge({
    from: k.program, rel: "REGISTERS", to: k.moving, derivation: "rule", rule: DI,
    props: { serviceType: k.iStrategy, lifetime: "scoped", serviceKey: "moving-average" },
    at: [program, 'builder.Services.AddKeyedScoped<IForecastStrategy, MovingAverageStrategy>("moving-average")'],
  });
  a.edge({
    from: k.program, rel: "REGISTERS", to: k.smoothing, derivation: "rule", rule: DI,
    props: { serviceType: k.iStrategy, lifetime: "scoped", serviceKey: "exp-smoothing" },
    at: [program, 'builder.Services.AddKeyedScoped<IForecastStrategy, ExponentialSmoothingStrategy>("exp-smoothing")'],
  });
  a.edge({ from: k.program, rel: "REGISTERS", to: k.consumer, derivation: "rule", rule: DI, props: { lifetime: "hostedService" }, at: [program, "builder.Services.AddHostedService<StatsUpdatedConsumer>()"] });

  // ---- code graph ----
  a.edge({ from: k.program, rel: "CALLS", to: k.mapEndpoints, at: [program, "MapForecastEndpoints()"] });
  for (const [impl, method, file, decl] of [[k.moving, k.movingForecast, mov, "MovingAverageStrategy : IForecastStrategy"], [k.smoothing, k.smoothingForecast, exp, "ExponentialSmoothingStrategy : IForecastStrategy"]]) {
    a.edge({ from: impl, rel: "IMPLEMENTS", to: k.iStrategy, at: [file, decl] });
    a.edge({ from: method, rel: "IMPLEMENTS", to: k.iForecast, at: [file, "public double Forecast(IReadOnlyList<int> weeklyUnits)"] });
  }
  a.edge({ from: k.getForecast, rel: "CALLS", to: k.resolve, at: [ep, "StrategyLoader.Resolve(services, name)"] });
  a.edge({ from: k.getForecast, rel: "CALLS", to: k.iForecast, resolution: "static-target", at: [ep, ".Forecast(series.Quantities)"] });
  a.edge({ from: k.getForecast, rel: "CALLS", to: k.getWeeklyAverage, at: [ep, "sales.GetWeeklyAverageAsync("] });
  // Calls on constructed generics target the original definition; type arguments go in props.
  a.edge({ from: k.getForecast, rel: "CALLS", to: k.listAsync, props: { typeArguments: [k.weeklyHistory] }, at: [ep, "history.ListAsync(h => h.ArticleId == articleId, cancellationToken)"] });
  a.edge({ from: k.getForecast, rel: "CALLS", to: k.addAsync, props: { typeArguments: [k.forecastEntry] }, at: [ep, "forecasts.AddAsync(new ForecastEntry"] });
  a.edge({ from: k.getForecast, rel: "CALLS", to: k.totalUnits, at: [ep, "series.TotalUnits()"] });
  a.edge({ from: k.movingForecast, rel: "CALLS", to: k.takeLast, at: [mov, "weeklyUnits.TakeLast(Window)"] });
  a.edge({ from: k.movingForecast, rel: "CALLS", to: k.average, at: [mov, ".Average()"] });
  a.edge({ from: k.smoothingForecast, rel: "CALLS", to: k.roundDouble, at: [exp, "Math.Round(level, 1)"] });
  a.edge({ from: k.resolve, rel: "CALLS", to: k.activator, at: [loader, "Activator.CreateInstance(type)"] });
  a.edge({ from: k.resolve, rel: "CALLS", to: k.keyedService, props: { typeArguments: [k.iStrategy] }, at: [loader, "services.GetRequiredKeyedService<IForecastStrategy>(name)"] });
  // Keyed resolution: any registered key may be chosen at runtime.
  for (const impl of [k.moving, k.smoothing]) {
    a.edge({
      from: k.resolve, rel: "REFERENCES", to: impl, derivation: "rule", rule: DI, resolution: "candidate",
      props: { serviceType: k.iStrategy, keySource: "runtime string (query parameter or Forecast:DefaultStrategy)" },
      at: [loader, "services.GetRequiredKeyedService<IForecastStrategy>(name)"],
    });
  }
  a.edge({
    from: k.resolve, rel: "REFERENCES", resolution: "unresolved", reason: "reflection",
    props: { expression: "Activator.CreateInstance(Type.GetType(name[PluginPrefix.Length..]))", expectedType: k.iStrategy },
    at: [loader, "Type.GetType(name[PluginPrefix.Length..], throwOnError: true)"],
  });

  // ---- calculations ----
  a.calc({
    key: k.movingForecast,
    output: { type: "float64", unit: { status: "unknown" } },
    inputs: [{ ref: "$0", type: "IReadOnlyList<int>" }],
    expr: ir.agg("avg", ir.call(k.takeLast, [ir.v("$0"), ir.lit(4, "int32")]), { type: "float64" }),
    attributes: {
      aggregation: { status: "known", value: "avg", at: [mov, ".Average()"] },
      timeBucket: { status: "partial", value: "suppliedByCaller", detail: { caller: k.getForecast, observed: "weekly_history rows ordered by WeekStart" }, at: [ep, "rows.OrderBy(r => r.WeekStart)"] },
      period: { status: "known", value: "lastElements", detail: { window: 4 }, at: [mov, "private const int Window = 4;"] },
      denominatorSource: { status: "known", value: "data", detail: { description: "number of elements in the window (at most 4)" }, at: [mov, "weeklyUnits.TakeLast(Window).Average()"] },
      filters: { status: "known", value: "none", at: [mov, "weeklyUnits.TakeLast(Window).Average()"] },
      rounding: { status: "known", value: "none", at: [mov, "return weeklyUnits.TakeLast(Window).Average();"] },
      numericType: { status: "known", value: "float64", detail: { input: "int32", note: "Enumerable.Average(IEnumerable<int>) returns double" }, at: [mov, "public double Forecast("] },
      nullHandling: { status: "known", value: "notApplicable", at: [mov, "IReadOnlyList<int> weeklyUnits"] },
      zeroDenominator: { status: "known", value: "throws", detail: { exception: "System.InvalidOperationException" }, at: [mov, ".Average()"] },
      currency: { status: "unknown" },
    },
    at: [mov, "public double Forecast(IReadOnlyList<int> weeklyUnits)"],
  });

  a.calc({
    key: k.smoothingForecast,
    output: { type: "float64", unit: { status: "unknown" } },
    inputs: [{ ref: "$0", type: "IReadOnlyList<int>" }],
    expr: ir.cond(
      ir.cmp("==", ir.m(ir.v("$0"), "Count", "int32"), ir.lit(0, "int32")),
      ir.lit("NaN", "float64"),
      ir.round(
        "halfEven",
        ir.fold(ir.v("$0"), {
          bind: "$1", acc: "$2", skip: 1, type: "float64",
          init: ir.conv("float64", ir.index(ir.v("$0"), ir.lit(0, "int32"), "int32")),
          step: ir.bin(
            "+",
            ir.bin("*", ir.lit(alpha, "float64"), ir.conv("float64", ir.v("$1")), "float64"),
            ir.bin("*", ir.bin("-", ir.lit(1, "float64"), ir.lit(alpha, "float64"), "float64"), ir.v("$2"), "float64"),
            "float64",
          ),
        }),
        ir.lit(1, "int32"),
        k.roundDouble,
      ),
    ),
    attributes: {
      aggregation: { status: "known", value: "exponentialSmoothing", detail: { alpha }, at: [exp, `private const double Alpha = ${alpha};`] },
      timeBucket: { status: "partial", value: "suppliedByCaller", detail: { caller: k.getForecast }, at: [ep, "rows.OrderBy(r => r.WeekStart)"] },
      period: { status: "known", value: "allSupplied", at: [exp, "foreach (var units in weeklyUnits.Skip(1))"] },
      denominatorSource: { status: "known", value: "none", at: [exp, "level = Alpha * units + (1 - Alpha) * level;"] },
      filters: { status: "known", value: "none", at: [exp, "foreach (var units in weeklyUnits.Skip(1))"] },
      rounding: { status: "known", value: "halfEven", detail: { digits: 1, api: "Math.Round(double, int)", note: "binary floating point" }, at: [exp, "Math.Round(level, 1)"] },
      numericType: { status: "known", value: "float64", at: [exp, "double level = weeklyUnits[0];"] },
      nullHandling: { status: "known", value: "notApplicable", at: [exp, "IReadOnlyList<int> weeklyUnits"] },
      zeroDenominator: { status: "known", value: "returnsNaN", detail: { description: "empty input returns double.NaN" }, at: [exp, "return double.NaN;"] },
      currency: { status: "unknown" },
    },
    at: [exp, "public double Forecast(IReadOnlyList<int> weeklyUnits)"],
  });

  a.calc({
    key: k.totalUnits,
    output: { type: "int32", unit: { status: "unknown" } },
    inputs: [{ ref: "$0", type: "IReadOnlyList<int>", origin: k.quantities }],
    expr: ir.fold(ir.v("$0"), { bind: "$1", acc: "$2", type: "int32", init: ir.lit(0, "int32"), step: ir.bin("+", ir.v("$2"), ir.v("$1"), "int32") }),
    attributes: {
      aggregation: { status: "known", value: "sum", at: [seriesStats, "total += units;"] },
      timeBucket: { status: "partial", value: "suppliedByCaller", at: [series, "public IReadOnlyList<int> Quantities { get; }"] },
      period: { status: "known", value: "allSupplied", at: [seriesStats, "foreach (var units in Quantities)"] },
      denominatorSource: { status: "known", value: "none", at: [seriesStats, "total += units;"] },
      filters: { status: "known", value: "none", at: [seriesStats, "foreach (var units in Quantities)"] },
      rounding: { status: "known", value: "none", at: [seriesStats, "return total;"] },
      numericType: { status: "known", value: "int32", at: [seriesStats, "var total = 0;"] },
      nullHandling: { status: "known", value: "notApplicable", at: [series, "IReadOnlyList<int> quantities"] },
      zeroDenominator: { status: "known", value: "notApplicable", at: [seriesStats, "return total;"] },
      currency: { status: "unknown" },
      // Unlike Enumerable.Sum (checked), a manual loop in the default unchecked context wraps on overflow.
      overflow: { status: "known", value: "wraps", detail: { context: "unchecked (C# default)" }, at: [seriesStats, "total += units;"] },
    },
    at: [seriesStats, "public int TotalUnits()"],
  });
  return [a];
}
