import { Artifact, ir, key } from "../lib.mjs";

const R = "api-cs";
const ROUTES = { name: "aspnetcore.routes", version: "1" };
const DI = { name: "dotnet.di", version: "1" };
const EF = { name: "efcore", version: "1" };
const SQLRULE = { name: "tsql.objects", version: "1" };

const DOMAIN = "Sales.Domain";
const API = "Sales.Api";
const ROWS = "System.Collections.Generic.IReadOnlyList{Sales.Domain.SaleRow}";
const RUNTIME = "System.Runtime";

export const k = {
  domainProj: key.proj(R, "src/Sales.Domain/Sales.Domain.csproj"),
  apiProj: key.proj(R, "src/Sales.Api/Sales.Api.csproj"),
  dbProj: key.proj(R, "database"),
  saleRow: key.cs(R, DOMAIN, "T:Sales.Domain.SaleRow"),
  saleRowQuantity: key.cs(R, DOMAIN, "P:Sales.Domain.SaleRow.Quantity"),
  isoWeeks: key.cs(R, DOMAIN, "T:Sales.Domain.IsoWeeks"),
  isoOf: key.cs(R, DOMAIN, "M:Sales.Domain.IsoWeeks.Of(System.DateOnly)"),
  countInPeriod: key.cs(R, DOMAIN, "M:Sales.Domain.IsoWeeks.CountInPeriod(System.DateOnly,System.DateOnly)"),
  isoGetYear: key.csExt(RUNTIME, "M:System.Globalization.ISOWeek.GetYear(System.DateTime)"),
  isoGetWeek: key.csExt(RUNTIME, "M:System.Globalization.ISOWeek.GetWeekOfYear(System.DateTime)"),
  mathRound2: key.csExt(RUNTIME, "M:System.Math.Round(System.Decimal,System.Int32)"),
  service: key.svc(R, API),
  weeklyEndpoint: key.http(`${R}/${API}`, "GET", "/api/articles/{}/weekly-sales"),
  healthEndpoint: key.http(`${R}/${API}`, "GET", "/api/health"),
  program: key.cs(R, API, "M:Program.{Main}$(System.String[])"),
  controller: key.cs(R, API, "T:Sales.Api.Controllers.ArticlesController"),
  response: key.cs(R, API, "T:Sales.Api.Controllers.WeeklySalesResponse"),
  getWeeklySales: key.cs(R, API, "M:Sales.Api.Controllers.ArticlesController.GetWeeklySales(System.String,System.DateOnly,System.DateOnly,System.Threading.CancellationToken)"),
  iCalculator: key.cs(R, API, "T:Sales.Api.Calculations.IWeeklySalesCalculator"),
  calculator: key.cs(R, API, "T:Sales.Api.Calculations.WeeklySalesCalculator"),
  iRepository: key.cs(R, API, "T:Sales.Api.Data.ISalesRepository"),
  repository: key.cs(R, API, "T:Sales.Api.Data.SalesRepository"),
  iGetSales: key.cs(R, API, "M:Sales.Api.Data.ISalesRepository.GetSalesAsync(System.String,System.DateOnly,System.DateOnly,System.Threading.CancellationToken)"),
  getSales: key.cs(R, API, "M:Sales.Api.Data.SalesRepository.GetSalesAsync(System.String,System.DateOnly,System.DateOnly,System.Threading.CancellationToken)"),
  saleEntity: key.cs(R, API, "T:Sales.Api.Data.SaleEntity"),
  dbContext: key.cs(R, API, "T:Sales.Api.Data.SalesDbContext"),
  // EF/SQL objects as seen from the API: store bound only by connection-string name.
  efSales: key.sql(key.unbound(R, API, "conn", "Sales"), "dbo.Sales"),
  // SQL objects as declared by the database project: store bound only by project name.
  dbSales: key.sql(key.unbound(R, "SalesDb", "dbproject", "SalesDb"), "dbo.Sales"),
  dbSalesQuantity: key.sql(key.unbound(R, "SalesDb", "dbproject", "SalesDb"), "dbo.Sales", "Quantity"),
  dbSalesSoldOn: key.sql(key.unbound(R, "SalesDb", "dbproject", "SalesDb"), "dbo.Sales", "SoldOn"),
  dbSalesArticleId: key.sql(key.unbound(R, "SalesDb", "dbproject", "SalesDb"), "dbo.Sales", "ArticleId"),
  dbArticleStats: key.sql(key.unbound(R, "SalesDb", "dbproject", "SalesDb"), "dbo.ArticleStats"),
  dbArticleStatsAvg: key.sql(key.unbound(R, "SalesDb", "dbproject", "SalesDb"), "dbo.ArticleStats", "AveragePerWeek"),
  fnWeekly: key.sql(key.unbound(R, "SalesDb", "dbproject", "SalesDb"), "dbo.fnWeeklySalesAverage"),
};

const methodName = (tag) => (tag === "v1-base" ? "AveragePerCalendarWeek" : "CalendarWeekAverage");
const calcPath = (tag) =>
  tag === "v1-base" ? "src/Sales.Api/Calculations/WeeklySalesCalculator.cs" : "src/Sales.Api/Calculations/Weekly/WeeklySalesCalculator.cs";
export const calcKeys = (tag) => ({
  iAverage: key.cs(R, API, `M:Sales.Api.Calculations.IWeeklySalesCalculator.${methodName(tag)}(${ROWS},System.DateOnly,System.DateOnly)`),
  average: key.cs(R, API, `M:Sales.Api.Calculations.WeeklySalesCalculator.${methodName(tag)}(${ROWS},System.DateOnly,System.DateOnly)`),
});

function domain(repo) {
  const a = new Artifact(repo, {
    unitPath: "src/Sales.Domain/Sales.Domain.csproj", unitName: DOMAIN, language: "csharp",
    sourcePrefix: "src/Sales.Domain", sourceExtensions: [".cs"],
  });
  const iso = "src/Sales.Domain/IsoWeeks.cs";
  const row = "src/Sales.Domain/SaleRow.cs";
  a.node({ key: k.domainProj, kind: "Project", name: DOMAIN, at: ["src/Sales.Domain/Sales.Domain.csproj", '<Project Sdk="Microsoft.NET.Sdk">'] });
  a.node({ key: k.saleRow, kind: "Type", name: "SaleRow", props: { typeKind: "record", accessibility: "public" }, at: [row, "public sealed record SaleRow"] });
  a.node({ key: k.saleRowQuantity, kind: "Property", name: "Quantity", signature: "decimal? Quantity", at: [row, "decimal? Quantity"] });
  a.node({ key: k.isoWeeks, kind: "Type", name: "IsoWeeks", props: { typeKind: "class", static: true }, at: [iso, "public static class IsoWeeks"] });
  a.node({ key: k.isoOf, kind: "Method", name: "Of", signature: "(int Year, int Week) Of(DateOnly date)", at: [iso, "public static (int Year, int Week) Of(DateOnly date)"] });
  a.node({ key: k.countInPeriod, kind: "Method", name: "CountInPeriod", signature: "int CountInPeriod(DateOnly from, DateOnly to)", at: [iso, "public static int CountInPeriod(DateOnly from, DateOnly to)"] });

  a.edge({ from: k.domainProj, rel: "DECLARES", to: k.saleRow, at: [row, "public sealed record SaleRow"] });
  a.edge({ from: k.domainProj, rel: "DECLARES", to: k.isoWeeks, at: [iso, "public static class IsoWeeks"] });
  a.edge({ from: k.saleRow, rel: "DECLARES", to: k.saleRowQuantity, at: [row, "decimal? Quantity"] });
  a.edge({ from: k.isoWeeks, rel: "DECLARES", to: k.isoOf, at: [iso, "public static (int Year, int Week) Of(DateOnly date)"] });
  a.edge({ from: k.isoWeeks, rel: "DECLARES", to: k.countInPeriod, at: [iso, "public static int CountInPeriod(DateOnly from, DateOnly to)"] });
  a.edge({ from: k.isoOf, rel: "CALLS", to: k.isoGetYear, at: [iso, "ISOWeek.GetYear(dateTime)"] });
  a.edge({ from: k.isoOf, rel: "CALLS", to: k.isoGetWeek, at: [iso, "ISOWeek.GetWeekOfYear(dateTime)"] });
  a.edge({ from: k.countInPeriod, rel: "CALLS", to: k.isoOf, at: [iso, "Of(day)"] });
  return a;
}

function api(repo, tag) {
  const a = new Artifact(repo, {
    unitPath: "src/Sales.Api/Sales.Api.csproj", unitName: API, language: "csharp",
    sourcePrefix: "src/Sales.Api", sourceExtensions: [".cs"], rules: [ROUTES, DI, EF],
  });
  const csproj = "src/Sales.Api/Sales.Api.csproj";
  const program = "src/Sales.Api/Program.cs";
  const ctl = "src/Sales.Api/Controllers/ArticlesController.cs";
  const icalc = "src/Sales.Api/Calculations/IWeeklySalesCalculator.cs";
  const calc = calcPath(tag);
  const repoFile = "src/Sales.Api/Data/SalesRepository.cs";
  const ctx = "src/Sales.Api/Data/SalesDbContext.cs";
  const entity = "src/Sales.Api/Data/SaleEntity.cs";
  const name = methodName(tag);
  const ck = calcKeys(tag);

  a.node({ key: k.apiProj, kind: "Project", name: API, at: [csproj, '<Project Sdk="Microsoft.NET.Sdk.Web">'] });
  a.node({ key: k.service, kind: "Service", name: API, props: { sdk: "Microsoft.NET.Sdk.Web" }, at: [csproj, '<Project Sdk="Microsoft.NET.Sdk.Web">'] });
  a.node({
    key: k.weeklyEndpoint, kind: "Endpoint", name: "GET /api/articles/{articleId}/weekly-sales",
    props: { method: "GET", route: "/api/articles/{articleId}/weekly-sales", normalizedRoute: "/api/articles/{}/weekly-sales", style: "controller" },
    at: [[ctl, '[Route("api/articles")]'], [ctl, '[HttpGet("{articleId}/weekly-sales")]']],
  });
  a.node({
    key: k.healthEndpoint, kind: "Endpoint", name: "GET /api/health",
    props: { method: "GET", route: "/api/health", normalizedRoute: "/api/health", style: "minimal-api" },
    at: [program, 'app.MapGet("/api/health", () => Results.Ok())'],
  });
  a.node({ key: k.program, kind: "Method", name: "<Main>$", props: { topLevelStatements: true }, at: [program, "var builder = WebApplication.CreateBuilder(args);"] });
  a.node({ key: k.controller, kind: "Type", name: "ArticlesController", props: { typeKind: "class" }, at: [ctl, "public sealed class ArticlesController"] });
  a.node({ key: k.response, kind: "Type", name: "WeeklySalesResponse", props: { typeKind: "record" }, at: [ctl, "public sealed record WeeklySalesResponse"] });
  a.node({ key: k.getWeeklySales, kind: "Method", name: "GetWeeklySales", at: [ctl, "public async Task<ActionResult<WeeklySalesResponse>> GetWeeklySales("] });
  a.node({ key: k.iCalculator, kind: "Type", name: "IWeeklySalesCalculator", props: { typeKind: "interface" }, at: [icalc, "public interface IWeeklySalesCalculator"] });
  a.node({ key: ck.iAverage, kind: "Method", name, at: [icalc, `decimal ${name}(`] });
  a.node({ key: k.calculator, kind: "Type", name: "WeeklySalesCalculator", props: { typeKind: "class" }, at: [calc, "public sealed class WeeklySalesCalculator"] });
  a.node({ key: ck.average, kind: "Method", name, at: [calc, `public decimal ${name}(`] });
  a.node({ key: k.iRepository, kind: "Type", name: "ISalesRepository", props: { typeKind: "interface" }, at: [repoFile, "public interface ISalesRepository"] });
  a.node({ key: k.iGetSales, kind: "Method", name: "GetSalesAsync", at: [repoFile, "Task<IReadOnlyList<SaleRow>> GetSalesAsync("] });
  a.node({ key: k.repository, kind: "Type", name: "SalesRepository", props: { typeKind: "class" }, at: [repoFile, "public sealed class SalesRepository"] });
  a.node({ key: k.getSales, kind: "Method", name: "GetSalesAsync", at: [repoFile, "public async Task<IReadOnlyList<SaleRow>> GetSalesAsync("] });
  a.node({ key: k.saleEntity, kind: "Type", name: "SaleEntity", props: { typeKind: "class" }, at: [entity, "public sealed class SaleEntity"] });
  a.node({ key: k.dbContext, kind: "Type", name: "SalesDbContext", props: { typeKind: "class" }, at: [ctx, "public sealed class SalesDbContext"] });

  // Project graph
  a.edge({ from: k.apiProj, rel: "DEPENDS_ON", to: k.domainProj, at: [csproj, '<ProjectReference Include="../Sales.Domain/Sales.Domain.csproj" />'] });

  // Architecture (rule-derived)
  a.edge({ from: k.service, rel: "EXPOSES", to: k.weeklyEndpoint, derivation: "rule", rule: ROUTES, at: [ctl, '[HttpGet("{articleId}/weekly-sales")]'] });
  a.edge({ from: k.service, rel: "EXPOSES", to: k.healthEndpoint, derivation: "rule", rule: ROUTES, at: [program, 'app.MapGet("/api/health"'] });
  // The health handler is a lambda: it is attributed to the nearest named container, the entry point.
  a.edge({ from: k.program, rel: "HANDLES", to: k.healthEndpoint, derivation: "rule", rule: ROUTES, props: { handler: "lambda" }, at: [program, 'app.MapGet("/api/health", () => Results.Ok())'] });
  a.edge({ from: k.getWeeklySales, rel: "HANDLES", to: k.weeklyEndpoint, derivation: "rule", rule: ROUTES, at: [ctl, '[HttpGet("{articleId}/weekly-sales")]'] });
  a.edge({
    from: k.program, rel: "REGISTERS", to: k.calculator, derivation: "rule", rule: DI,
    props: { serviceType: k.iCalculator, lifetime: "scoped" },
    at: [program, "builder.Services.AddScoped<IWeeklySalesCalculator, WeeklySalesCalculator>()"],
  });
  a.edge({
    from: k.program, rel: "REGISTERS", to: k.repository, derivation: "rule", rule: DI,
    props: { serviceType: k.iRepository, lifetime: "scoped" },
    at: [program, "builder.Services.AddScoped<ISalesRepository, SalesRepository>()"],
  });
  a.edge({
    from: k.saleEntity, rel: "MAPS_TO", to: k.efSales, derivation: "rule", rule: EF,
    resolution: "unresolved", reason: "configuration",
    props: { connectionName: "Sales", schema: "dbo", table: "Sales" },
    at: [[ctx, 'entity.ToTable("Sales", "dbo")'], [program, 'GetConnectionString("Sales")']],
  });
  a.edge({
    from: k.getSales, rel: "READS", to: k.efSales, derivation: "rule", rule: EF,
    resolution: "unresolved", reason: "configuration",
    props: { via: k.dbContext, connectionName: "Sales" },
    at: [repoFile, "db.Sales"],
  });

  // Code graph
  a.edge({ from: k.calculator, rel: "IMPLEMENTS", to: k.iCalculator, at: [calc, "WeeklySalesCalculator : IWeeklySalesCalculator"] });
  a.edge({ from: ck.average, rel: "IMPLEMENTS", to: ck.iAverage, at: [calc, `public decimal ${name}(`] });
  a.edge({ from: k.repository, rel: "IMPLEMENTS", to: k.iRepository, at: [repoFile, "SalesRepository(SalesDbContext db) : ISalesRepository"] });
  a.edge({ from: k.getSales, rel: "IMPLEMENTS", to: k.iGetSales, at: [repoFile, "public async Task<IReadOnlyList<SaleRow>> GetSalesAsync("] });
  // Interface dispatch: the compiler proves the static target only.
  a.edge({ from: k.getWeeklySales, rel: "CALLS", to: ck.iAverage, resolution: "static-target", at: [ctl, `calculator.${name}(rows, from, to)`] });
  a.edge({ from: k.getWeeklySales, rel: "CALLS", to: k.iGetSales, resolution: "static-target", at: [ctl, "repository.GetSalesAsync(articleId, from, to, cancellationToken)"] });
  a.edge({ from: ck.average, rel: "CALLS", to: k.countInPeriod, at: [calc, "IsoWeeks.CountInPeriod(from, to)"] });
  a.edge({ from: ck.average, rel: "CALLS", to: k.mathRound2, at: [calc, "Math.Round(total / weeks, 2)"] });

  // Calculation
  const row = "$3";
  const inPeriod = [ir.cmp(">=", ir.m(ir.v(row), "SoldOn"), ir.v("$1")), ir.cmp("<=", ir.m(ir.v(row), "SoldOn"), ir.v("$2"))];
  const filter = tag === "v3-filter" ? ir.and(...inPeriod, ir.not(ir.m(ir.v(row), "IsReturn"))) : ir.and(...inPeriod);
  const weeks = ir.call(k.countInPeriod, [ir.v("$1"), ir.v("$2")], "int32");
  const total = ir.agg("sum", ir.v("$0"), { bind: row, filter, value: ir.coalesce(ir.m(ir.v(row), "Quantity", "decimal"), ir.lit(0, "decimal")), type: "decimal" });
  const whereNeedle = tag === "v3-filter" ? ".Where(r => r.SoldOn >= from && r.SoldOn <= to && !r.IsReturn)" : ".Where(r => r.SoldOn >= from && r.SoldOn <= to)";
  a.calc({
    key: ck.average,
    includes: [k.countInPeriod, k.isoOf],
    output: { type: "decimal", unit: { status: "unknown" } },
    inputs: [
      { ref: "$0", type: "IReadOnlyList<SaleRow>", origin: k.saleRow },
      { ref: "$1", type: "DateOnly" },
      { ref: "$2", type: "DateOnly" },
    ],
    expr: ir.cond(
      ir.cmp("==", weeks, ir.lit(0, "int32")),
      ir.lit(0, "decimal"),
      ir.round("halfEven", ir.bin("/", total, ir.conv("decimal", weeks), "decimal"), ir.lit(2, "int32"), k.mathRound2),
    ),
    attributes: {
      aggregation: { status: "known", value: "sum", at: [calc, ".Sum(r => r.Quantity ?? 0m)"] },
      timeBucket: { status: "known", value: "isoWeek", detail: { source: "System.Globalization.ISOWeek" }, at: ["src/Sales.Domain/IsoWeeks.cs", "ISOWeek.GetWeekOfYear(dateTime)"] },
      period: { status: "known", value: "requested", detail: { bounds: "[from, to] inclusive" }, at: [calc, whereNeedle] },
      denominatorSource: { status: "known", value: "period", detail: { description: "number of ISO weeks touched by [from, to]" }, at: [calc, "IsoWeeks.CountInPeriod(from, to)"] },
      filters: {
        status: "known",
        value: tag === "v3-filter" ? "soldOn in [from,to] and not isReturn" : "soldOn in [from,to]",
        at: [calc, whereNeedle],
      },
      rounding: { status: "known", value: "halfEven", detail: { digits: 2, api: "Math.Round(decimal, int)" }, at: [calc, "Math.Round(total / weeks, 2)"] },
      numericType: { status: "known", value: "decimal", at: [calc, `public decimal ${name}(`] },
      nullHandling: { status: "known", value: "nullAsZero", at: [calc, "r.Quantity ?? 0m"] },
      zeroDenominator: { status: "known", value: "returnsZero", at: [calc, "return 0m;"] },
      currency: { status: "unknown" },
    },
    at: [calc, `public decimal ${name}(`],
  });
  return a;
}

function database(repo) {
  const a = new Artifact(repo, {
    unitPath: "database", unitName: "SalesDb", language: "sql",
    sourcePrefix: "database", sourceExtensions: [".sql"], rules: [SQLRULE],
  });
  const sales = "database/tables/Sales.sql";
  const stats = "database/tables/ArticleStats.sql";
  const fn = "database/functions/fnWeeklySalesAverage.sql";
  a.node({ key: k.dbProj, kind: "Project", name: "SalesDb", props: { dialect: "tsql" }, at: [sales, "CREATE TABLE dbo.Sales"] });
  a.node({ key: k.dbSales, kind: "Table", name: "dbo.Sales", at: [sales, "CREATE TABLE dbo.Sales"] });
  a.node({ key: k.dbSalesArticleId, kind: "Column", name: "ArticleId", signature: "nvarchar(50) NOT NULL", at: [sales, "ArticleId nvarchar(50)"] });
  a.node({ key: k.dbSalesSoldOn, kind: "Column", name: "SoldOn", signature: "date NOT NULL", at: [sales, "SoldOn    date"] });
  a.node({ key: k.dbSalesQuantity, kind: "Column", name: "Quantity", signature: "decimal(18, 3) NULL", at: [sales, "Quantity  decimal(18, 3)"] });
  a.node({ key: k.dbArticleStats, kind: "Table", name: "dbo.ArticleStats", at: [stats, "CREATE TABLE dbo.ArticleStats"] });
  a.node({ key: k.dbArticleStatsAvg, kind: "Column", name: "AveragePerWeek", signature: "decimal(18, 2) NOT NULL", at: [stats, "AveragePerWeek decimal(18, 2)"] });
  a.node({ key: k.fnWeekly, kind: "Routine", name: "dbo.fnWeeklySalesAverage", props: { routineKind: "inline-table-valued-function" }, at: [fn, "CREATE FUNCTION dbo.fnWeeklySalesAverage"] });

  for (const [t, at] of [[k.dbSales, [sales, "CREATE TABLE dbo.Sales"]], [k.dbArticleStats, [stats, "CREATE TABLE dbo.ArticleStats"]], [k.fnWeekly, [fn, "CREATE FUNCTION dbo.fnWeeklySalesAverage"]]]) {
    a.edge({ from: k.dbProj, rel: "DECLARES", to: t, derivation: "rule", rule: SQLRULE, at });
  }
  a.edge({ from: k.dbSales, rel: "DECLARES", to: k.dbSalesArticleId, derivation: "rule", rule: SQLRULE, at: [sales, "ArticleId nvarchar(50)"] });
  a.edge({ from: k.dbSales, rel: "DECLARES", to: k.dbSalesSoldOn, derivation: "rule", rule: SQLRULE, at: [sales, "SoldOn    date"] });
  a.edge({ from: k.dbSales, rel: "DECLARES", to: k.dbSalesQuantity, derivation: "rule", rule: SQLRULE, at: [sales, "Quantity  decimal(18, 3)"] });
  a.edge({ from: k.dbArticleStats, rel: "DECLARES", to: k.dbArticleStatsAvg, derivation: "rule", rule: SQLRULE, at: [stats, "AveragePerWeek decimal(18, 2)"] });
  a.edge({ from: k.fnWeekly, rel: "READS", to: k.dbSales, derivation: "rule", rule: SQLRULE, at: [fn, "FROM dbo.Sales AS s"] });
  a.edge({ from: k.fnWeekly, rel: "READS", to: k.dbSalesQuantity, derivation: "rule", rule: SQLRULE, at: [fn, "s.Quantity"] });
  a.edge({ from: k.fnWeekly, rel: "READS", to: k.dbSalesSoldOn, derivation: "rule", rule: SQLRULE, at: [fn, "s.SoldOn BETWEEN @From AND @To"] });
  a.edge({ from: k.fnWeekly, rel: "READS", to: k.dbSalesArticleId, derivation: "rule", rule: SQLRULE, at: [fn, "s.ArticleId = @ArticleId"] });

  const row = "$4";
  const days = ir.call(key.sqlBuiltin("tsql", "DATEDIFF"), [ir.lit("day"), ir.v("$1"), ir.v("$2")], "int32");
  const buckets = ir.bin("+", ir.bin("/", days, ir.lit(7, "int32"), "int32"), ir.lit(1, "int32"), "int32");
  a.calc({
    key: k.fnWeekly,
    output: { type: "decimal", unit: { status: "unknown" } },
    inputs: [
      { ref: "$0", type: "nvarchar(50)" },
      { ref: "$1", type: "date" },
      { ref: "$2", type: "date" },
      { ref: "$3", type: "table", origin: k.dbSales },
    ],
    expr: ir.round(
      "halfAwayFromZero",
      ir.bin(
        "/",
        ir.agg("sum", ir.v("$3"), {
          bind: row,
          filter: ir.and(
            ir.cmp("==", ir.m(ir.v(row), "ArticleId"), ir.v("$0")),
            ir.cmp(">=", ir.m(ir.v(row), "SoldOn"), ir.v("$1")),
            ir.cmp("<=", ir.m(ir.v(row), "SoldOn"), ir.v("$2")),
          ),
          value: ir.coalesce(ir.m(ir.v(row), "Quantity", "decimal"), ir.lit(0, "int32")),
          type: "decimal",
        }),
        ir.call(key.sqlBuiltin("tsql", "NULLIF"), [buckets, ir.lit(0, "int32")], "int32"),
        "decimal",
      ),
      ir.lit(2, "int32"),
      key.sqlBuiltin("tsql", "ROUND"),
    ),
    attributes: {
      aggregation: { status: "known", value: "sum", at: [fn, "SUM(ISNULL(s.Quantity, 0))"] },
      timeBucket: {
        status: "known", value: "sevenDayFromPeriodStart",
        detail: { description: "DATEDIFF(day)/7 + 1 with integer division; not ISO calendar weeks" },
        at: [fn, "DATEDIFF(day, @From, @To) / 7 + 1"],
      },
      period: { status: "known", value: "requested", detail: { bounds: "[@From, @To] inclusive (BETWEEN)" }, at: [fn, "s.SoldOn BETWEEN @From AND @To"] },
      denominatorSource: { status: "known", value: "period", at: [fn, "DATEDIFF(day, @From, @To) / 7 + 1"] },
      filters: { status: "known", value: "articleId = @ArticleId and soldOn in [@From,@To]", at: [fn, "WHERE s.ArticleId = @ArticleId"] },
      rounding: { status: "known", value: "halfAwayFromZero", detail: { digits: 2, api: "T-SQL ROUND(decimal, 2)" }, at: [fn, "ROUND("] },
      numericType: { status: "known", value: "decimal", detail: { denominator: "int32 (integer division)" }, at: [sales, "Quantity  decimal(18, 3)"] },
      nullHandling: { status: "known", value: "nullAsZero", at: [fn, "ISNULL(s.Quantity, 0)"] },
      zeroDenominator: { status: "known", value: "returnsNull", at: [fn, "NULLIF(DATEDIFF(day, @From, @To) / 7 + 1, 0)"] },
      currency: { status: "unknown" },
    },
    at: [fn, "CREATE FUNCTION dbo.fnWeeklySalesAverage"],
  });
  return a;
}

export default function apiCs(repo, tag) {
  return [domain(repo), api(repo, tag), database(repo)];
}
