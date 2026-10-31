import { Artifact, ir, key } from "../lib.mjs";

const R = "worker-cs";
const W = "Sales.Worker";
const RUNTIME = "System.Runtime";
const DI = { name: "dotnet.di", version: "1" };
const DAPPER = { name: "dapper.sql", version: "1" };
const SB = { name: "azure.servicebus", version: "1" };

const RECS = "System.Collections.Generic.IReadOnlyCollection{Sales.Worker.Calculations.SaleRecord}";
export const k = {
  proj: key.proj(R, "src/Sales.Worker/Sales.Worker.csproj"),
  service: key.svc(R, W),
  program: key.cs(R, W, "M:Program.<Main>$(System.String[])"),
  calculator: key.cs(R, W, "T:Sales.Worker.Calculations.ArticleStatsCalculator"),
  average: key.cs(R, W, `M:Sales.Worker.Calculations.ArticleStatsCalculator.AveragePerActiveWeek(${RECS})`),
  saleRecord: key.cs(R, W, "T:Sales.Worker.Calculations.SaleRecord"),
  iRepo: key.cs(R, W, "T:Sales.Worker.Data.IStatsRepository"),
  repo: key.cs(R, W, "T:Sales.Worker.Data.StatsRepository"),
  iGetSince: key.cs(R, W, "M:Sales.Worker.Data.IStatsRepository.GetSalesSinceAsync(System.DateTime)"),
  getSince: key.cs(R, W, "M:Sales.Worker.Data.StatsRepository.GetSalesSinceAsync(System.DateTime)"),
  iSave: key.cs(R, W, "M:Sales.Worker.Data.IStatsRepository.SaveAverageAsync(System.String,System.Decimal)"),
  save: key.cs(R, W, "M:Sales.Worker.Data.StatsRepository.SaveAverageAsync(System.String,System.Decimal)"),
  publisher: key.cs(R, W, "T:Sales.Worker.Messaging.StatsPublisher"),
  publish: key.cs(R, W, "M:Sales.Worker.Messaging.StatsPublisher.PublishAsync(Sales.Worker.Messaging.ArticleStatsUpdated,System.Threading.CancellationToken)"),
  message: key.cs(R, W, "T:Sales.Worker.Messaging.ArticleStatsUpdated"),
  job: key.cs(R, W, "T:Sales.Worker.Jobs.NightlyStatsJob"),
  execute: key.cs(R, W, "M:Sales.Worker.Jobs.NightlyStatsJob.ExecuteAsync(System.Threading.CancellationToken)"),
  topic: key.chan("servicebus", key.unbound(R, W, "config", "ServiceBus:ConnectionString"), "topic/article-stats-updated"),
  sales: key.sql(key.unbound(R, W, "conn", "Sales"), "dbo.Sales"),
  stats: key.sql(key.unbound(R, W, "conn", "Sales"), "dbo.ArticleStats"),
  mathRound3: key.csExt(RUNTIME, "M:System.Math.Round(System.Decimal,System.Int32,System.MidpointRounding)"),
  isoGetYear: key.csExt(RUNTIME, "M:System.Globalization.ISOWeek.GetYear(System.DateTime)"),
  isoGetWeek: key.csExt(RUNTIME, "M:System.Globalization.ISOWeek.GetWeekOfYear(System.DateTime)"),
  normalizer: key.cs(R, W, "T:Sales.Worker.Calculations.CurrencyNormalizer"),
  toSek: key.cs(R, W, "M:Sales.Worker.Calculations.CurrencyNormalizer.ToSek(System.Decimal,System.String)"),
};

export default function workerCs(repo, tag) {
  const a = new Artifact(repo, {
    unitPath: "src/Sales.Worker/Sales.Worker.csproj", unitName: W, language: "csharp",
    sourcePrefix: "src/Sales.Worker", sourceExtensions: [".cs"], rules: [DI, DAPPER, SB],
  });
  const csproj = "src/Sales.Worker/Sales.Worker.csproj";
  const program = "src/Sales.Worker/Program.cs";
  const calc = "src/Sales.Worker/Calculations/ArticleStatsCalculator.cs";
  const rec = "src/Sales.Worker/Calculations/SaleRecord.cs";
  const data = "src/Sales.Worker/Data/StatsRepository.cs";
  const pub = "src/Sales.Worker/Messaging/StatsPublisher.cs";
  const msg = "src/Sales.Worker/Messaging/ArticleStatsUpdated.cs";
  const job = "src/Sales.Worker/Jobs/NightlyStatsJob.cs";

  a.node({ key: k.proj, kind: "Project", name: W, at: [csproj, '<Project Sdk="Microsoft.NET.Sdk.Worker">'] });
  a.node({ key: k.service, kind: "Service", name: W, props: { sdk: "Microsoft.NET.Sdk.Worker" }, at: [csproj, '<Project Sdk="Microsoft.NET.Sdk.Worker">'] });
  a.node({ key: k.program, kind: "Method", name: "<Main>$", props: { topLevelStatements: true }, at: [program, "var builder = Host.CreateApplicationBuilder(args);"] });
  a.node({ key: k.calculator, kind: "Type", name: "ArticleStatsCalculator", props: { typeKind: "class", static: true }, at: [calc, "public static class ArticleStatsCalculator"] });
  a.node({ key: k.average, kind: "Method", name: "AveragePerActiveWeek", at: [calc, "public static decimal AveragePerActiveWeek("] });
  a.node({ key: k.saleRecord, kind: "Type", name: "SaleRecord", props: { typeKind: "record" }, at: [rec, "public sealed record SaleRecord"] });
  a.node({ key: k.iRepo, kind: "Type", name: "IStatsRepository", props: { typeKind: "interface" }, at: [data, "public interface IStatsRepository"] });
  a.node({ key: k.iGetSince, kind: "Method", name: "GetSalesSinceAsync", at: [data, "Task<IReadOnlyList<SaleRecord>> GetSalesSinceAsync(DateTime since);"] });
  a.node({ key: k.iSave, kind: "Method", name: "SaveAverageAsync", at: [data, "Task SaveAverageAsync(string articleId, decimal averagePerWeek);"] });
  a.node({ key: k.repo, kind: "Type", name: "StatsRepository", props: { typeKind: "class" }, at: [data, "public sealed class StatsRepository"] });
  a.node({ key: k.getSince, kind: "Method", name: "GetSalesSinceAsync", at: [data, "public async Task<IReadOnlyList<SaleRecord>> GetSalesSinceAsync(DateTime since)"] });
  a.node({ key: k.save, kind: "Method", name: "SaveAverageAsync", at: [data, "public async Task SaveAverageAsync(string articleId, decimal averagePerWeek)"] });
  a.node({ key: k.publisher, kind: "Type", name: "StatsPublisher", props: { typeKind: "class" }, at: [pub, "public sealed class StatsPublisher"] });
  a.node({ key: k.publish, kind: "Method", name: "PublishAsync", at: [pub, "public async Task PublishAsync("] });
  a.node({ key: k.message, kind: "Type", name: "ArticleStatsUpdated", props: { typeKind: "record", messageContract: true }, at: [msg, "public sealed record ArticleStatsUpdated"] });
  a.node({ key: k.job, kind: "Type", name: "NightlyStatsJob", props: { typeKind: "class" }, at: [job, "public sealed class NightlyStatsJob"] });
  a.node({ key: k.execute, kind: "Method", name: "ExecuteAsync", at: [job, "protected override async Task ExecuteAsync(CancellationToken stoppingToken)"] });
  a.node({
    key: k.topic, kind: "Channel", name: "article-stats-updated",
    props: { provider: "azure-servicebus", entityType: "topic", namespaceConfigKey: "ServiceBus:ConnectionString" },
    at: [[pub, 'private const string TopicName = "article-stats-updated";'], [program, 'builder.Configuration["ServiceBus:ConnectionString"]']],
  });

  a.edge({ from: k.repo, rel: "IMPLEMENTS", to: k.iRepo, at: [data, "StatsRepository(string connectionString) : IStatsRepository"] });
  a.edge({ from: k.getSince, rel: "IMPLEMENTS", to: k.iGetSince, at: [data, "public async Task<IReadOnlyList<SaleRecord>> GetSalesSinceAsync(DateTime since)"] });
  a.edge({ from: k.save, rel: "IMPLEMENTS", to: k.iSave, at: [data, "public async Task SaveAverageAsync(string articleId, decimal averagePerWeek)"] });
  a.edge({ from: k.execute, rel: "CALLS", to: k.iGetSince, resolution: "static-target", at: [job, "repository.GetSalesSinceAsync(since)"] });
  a.edge({ from: k.execute, rel: "CALLS", to: k.average, at: [job, "ArticleStatsCalculator.AveragePerActiveWeek(article.ToList())"] });
  a.edge({ from: k.execute, rel: "CALLS", to: k.iSave, resolution: "static-target", at: [job, "repository.SaveAverageAsync(article.Key, average)"] });
  a.edge({ from: k.execute, rel: "CALLS", to: k.publish, at: [job, "publisher.PublishAsync("] });
  a.edge({ from: k.average, rel: "CALLS", to: k.mathRound3, at: [calc, "Math.Round(total / activeWeeks, 2, MidpointRounding.AwayFromZero)"] });
  a.edge({ from: k.average, rel: "CALLS", to: k.isoGetYear, at: [calc, "ISOWeek.GetYear(r.SoldOn)"] });
  a.edge({ from: k.average, rel: "CALLS", to: k.isoGetWeek, at: [calc, "ISOWeek.GetWeekOfYear(r.SoldOn)"] });
  a.edge({
    from: k.program, rel: "REGISTERS", to: k.repo, derivation: "rule", rule: DI,
    props: { serviceType: k.iRepo, lifetime: "singleton", factory: true },
    at: [program, "builder.Services.AddSingleton<IStatsRepository>("],
  });
  a.edge({
    from: k.program, rel: "REGISTERS", to: k.job, derivation: "rule", rule: DI,
    props: { lifetime: "hostedService" },
    at: [program, "builder.Services.AddHostedService<NightlyStatsJob>()"],
  });
  a.edge({
    from: k.getSince, rel: "READS", to: k.sales, derivation: "rule", rule: DAPPER,
    resolution: "unresolved", reason: "configuration",
    props: { connectionName: "Sales", columns: ["ArticleId", "SoldOn", "Quantity"], sqlConstant: "SelectSales" },
    at: [[data, '"SELECT ArticleId, SoldOn, Quantity FROM dbo.Sales WHERE SoldOn >= @Since"'], [program, 'GetConnectionString("Sales")']],
  });
  a.edge({
    from: k.save, rel: "WRITES", to: k.stats, derivation: "rule", rule: DAPPER,
    resolution: "unresolved", reason: "configuration",
    props: { connectionName: "Sales", columns: ["AveragePerWeek", "UpdatedAt"], sqlConstant: "UpdateStats" },
    at: [[data, '"UPDATE dbo.ArticleStats SET AveragePerWeek'], [program, 'GetConnectionString("Sales")']],
  });
  a.edge({
    from: k.publish, rel: "PUBLISHES", to: k.topic, derivation: "rule", rule: SB,
    resolution: "unresolved", reason: "configuration",
    props: { contract: k.message, subject: "ArticleStatsUpdated", serialization: "System.Text.Json" },
    at: [[pub, "client.CreateSender(TopicName)"], [pub, "sender.SendMessageAsync("]],
  });

  const r1 = "$1";
  const r2 = "$2";
  const hasQuantity = (r) => ir.cmp("!=", ir.m(ir.v(r), "Quantity"), ir.lit(null));
  const weeks = ir.agg("countDistinct", ir.v("$0"), {
    bind: r1, filter: hasQuantity(r1),
    value: ir.tuple(
      ir.call(k.isoGetYear, [ir.m(ir.v(r1), "SoldOn")], "int32"),
      ir.call(k.isoGetWeek, [ir.m(ir.v(r1), "SoldOn")], "int32"),
    ),
    type: "int32",
  });
  const total = ir.agg("sum", ir.v("$0"), { bind: r2, filter: hasQuantity(r2), value: ir.m(ir.v(r2), "Quantity", "decimal"), type: "decimal" });
  a.calc({
    key: k.average,
    output: { type: "decimal", unit: { status: "unknown" } },
    inputs: [{ ref: "$0", type: "IReadOnlyCollection<SaleRecord>", origin: k.saleRecord }],
    expr: ir.cond(
      ir.cmp("==", weeks, ir.lit(0, "int32")),
      ir.lit(0, "decimal"),
      ir.round("halfAwayFromZero", ir.bin("/", total, ir.conv("decimal", weeks), "decimal"), ir.lit(2, "int32"), k.mathRound3),
    ),
    attributes: {
      aggregation: { status: "known", value: "sum", at: [calc, "withQuantity.Sum(r => r.Quantity!.Value)"] },
      timeBucket: { status: "known", value: "isoWeek", detail: { source: "System.Globalization.ISOWeek" }, at: [calc, "ISOWeek.GetWeekOfYear(r.SoldOn)"] },
      period: {
        status: "partial", value: "suppliedByCaller",
        detail: { caller: k.execute, observed: "last 91 days from UTC today" },
        at: [job, "DateTime.UtcNow.Date.AddDays(-91)"],
      },
      denominatorSource: { status: "known", value: "data", detail: { description: "distinct ISO weeks with at least one non-null quantity" }, at: [calc, ".Distinct()"] },
      filters: { status: "known", value: "quantity is not null", at: [calc, "rows.Where(r => r.Quantity.HasValue)"] },
      rounding: { status: "known", value: "halfAwayFromZero", detail: { digits: 2, api: "Math.Round(decimal, int, MidpointRounding)" }, at: [calc, "MidpointRounding.AwayFromZero"] },
      numericType: { status: "known", value: "decimal", at: [calc, "public static decimal AveragePerActiveWeek("] },
      nullHandling: { status: "known", value: "excluded", at: [calc, "rows.Where(r => r.Quantity.HasValue)"] },
      zeroDenominator: { status: "known", value: "returnsZero", at: [calc, "return 0m;"] },
      currency: { status: "unknown" },
    },
    at: [calc, "public static decimal AveragePerActiveWeek("],
  });

  if (tag === "v2-broken") {
    const norm = "src/Sales.Worker/Calculations/CurrencyNormalizer.cs";
    a.node({ key: k.normalizer, kind: "Type", name: "CurrencyNormalizer", props: { typeKind: "class", static: true }, at: [norm, "public static class CurrencyNormalizer"] });
    a.node({ key: k.toSek, kind: "Method", name: "ToSek", at: [norm, "public static decimal ToSek(decimal amount, string currency)"] });
    a.edge({ from: k.toSek, rel: "CALLS", resolution: "unresolved", reason: "missing-reference", props: { expression: "ExchangeRates.Convert" }, at: [norm, 'ExchangeRates.Convert(amount, currency, "SEK")'] });
    a.diag({
      severity: "error", code: "MISSING_PROJECT_REFERENCE", affectsCoverage: true, path: csproj,
      message: "Referenced project ../Sales.Shared/Sales.Shared.csproj does not exist in the repository",
      at: [csproj, '<ProjectReference Include="../Sales.Shared/Sales.Shared.csproj" />'],
    });
    a.diag({
      severity: "error", code: "COMPILER_CS0234", affectsCoverage: true, path: norm,
      message: "The type or namespace name 'Shared' does not exist in the namespace 'Sales'",
      at: [norm, "using Sales.Shared.Money;"],
    });
    a.markIncomplete(norm);
  }
  return [a];
}
