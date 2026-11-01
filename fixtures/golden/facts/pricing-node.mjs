import { Artifact, ir, key } from "../lib.mjs";

const R = "pricing-node";
const PKG = "@fixture/pricing";
const DBUNIT = "pricing-db";
const EXPRESS = { name: "express.routes", version: "1" };
const AXIOS = { name: "axios", version: "1" };
const OPENAPI = { name: "openapi.client", version: "1" };
const PG = { name: "pg.sql", version: "1" };
const FETCH = { name: "web.fetch", version: "1" };
const SB = { name: "azure.servicebus", version: "1" };
const HANDLER_MAP = { name: "ts.handler-map", version: "1" };
const SQLRULE = { name: "postgres.objects", version: "1" };

const ts = (mod, decl, meaning) => key.ts(R, PKG, mod, decl, meaning);
const pgStore = key.unbound(R, PKG, "env", "PRICING_DATABASE_URL");
const dbStore = key.unbound(R, DBUNIT, "dbproject", DBUNIT);

export const priceForDemandModule = (tag) => (tag === "v1-base" ? "src/pricing/priceForDemand.ts" : "src/pricing/demand.ts");

export const k = {
  proj: key.proj(R, "tsconfig.json"),
  service: key.svc(R, PKG),
  serverModule: ts("src/server.ts", "(module)", "namespace"),
  reportsModule: ts("src/routes/reports.ts", "(module)", "namespace"),
  getPrice: ts("src/handlers/getPrice.ts", "getPrice"),
  recalculate: ts("src/handlers/recalculate.ts", "recalculate"),
  getForecast: ts("src/clients/forecastClient.ts", "getForecast"),
  forecastResponse: ts("src/clients/forecastClient.ts", "ForecastResponse", "type"),
  salesApi: ts("src/generated/salesApi.ts", "SalesApi", "type"),
  getWeeklySales: ts("src/generated/salesApi.ts", "SalesApi.getWeeklySales"),
  request: ts("src/generated/salesApi.ts", "SalesApi.request"),
  findPrice: ts("src/db/priceRepository.ts", "findPrice"),
  listPrices: ts("src/db/priceRepository.ts", "listPrices"),
  saveSignal: ts("src/db/priceRepository.ts", "saveSignal"),
  onStatsUpdated: ts("src/messaging/statsListener.ts", "onStatsUpdated"),
  startListener: ts("src/messaging/statsListener.ts", "startStatsListener"),
  demandRatio: ts("src/pricing/demandRatio.ts", "demandRatio"),
  priceForDemand: (tag) => ts(priceForDemandModule(tag), "priceForDemand"),
  weeklyRevenueSek: ts("src/legacy/revenue.js", "weeklyRevenueSek"),
  getEndpoint: key.http(`${R}/${PKG}`, "GET", "/api/pricing/{}"),
  recalcEndpoint: key.http(`${R}/${PKG}`, "POST", "/api/pricing/{}/recalculate"),
  reportEndpoint: key.http(`${R}/${PKG}`, "GET", "/api/pricing/reports/prices"),
  forecastEndpoint: key.http(key.unbound(R, PKG, "env", "FORECAST_API_URL"), "GET", "/api/forecasts/{}"),
  salesEndpoint: key.http(key.unbound(R, PKG, "env", "SALES_API_URL"), "GET", "/api/articles/{}/weekly-sales"),
  // Namespace and queue are literal values in config/default.json: this channel is bound, not unbound.
  queue: key.chan("servicebus", "sb-pricing.servicebus.windows.net", "queue/article-stats-updated"),
  prices: key.sql(pgStore, "pricing.prices"),
  signals: key.sql(pgStore, "pricing.demand_signals"),
  mathTrunc: key.tsExt("typescript", "lib.es2015.core.d.ts", "Math.trunc"),
  mathMin: key.tsExt("typescript", "lib.es5.d.ts", "Math.min"),
  mathMax: key.tsExt("typescript", "lib.es5.d.ts", "Math.max"),
  toFixed: key.tsExt("typescript", "lib.es5.d.ts", "Number.toFixed"),
  db: {
    proj: key.proj(R, "db"),
    prices: key.sql(dbStore, "pricing.prices"),
    ledger: key.sql(dbStore, "pricing.sales_ledger"),
    ledgerAmount: key.sql(dbStore, "pricing.sales_ledger", "amount_ore"),
    ledgerQuantity: key.sql(dbStore, "pricing.sales_ledger", "quantity"),
    ledgerCurrency: key.sql(dbStore, "pricing.sales_ledger", "currency"),
    ledgerSoldAt: key.sql(dbStore, "pricing.sales_ledger", "sold_at"),
    ledgerArticleId: key.sql(dbStore, "pricing.sales_ledger", "article_id"),
    signals: key.sql(dbStore, "pricing.demand_signals"),
    weeklyRevenue: key.sql(dbStore, "pricing.weekly_revenue"),
    dateTrunc: key.sqlBuiltin("postgresql", "date_trunc"),
    nullif: key.sqlBuiltin("postgresql", "NULLIF"),
    round: key.sqlBuiltin("postgresql", "ROUND"),
  },
};

function service(repo, tag) {
  const a = new Artifact(repo, {
    unitPath: "tsconfig.json", unitName: PKG, language: "typescript",
    sourcePrefix: "src", sourceExtensions: [".ts", ".js"],
    inputPaths: ["src", "config", "package.json", "package-lock.json", "tsconfig.json"],
    rules: [EXPRESS, AXIOS, OPENAPI, FETCH, PG, SB, HANDLER_MAP],
  });
  const server = "src/server.ts";
  const routes = "src/routes/pricing.ts";
  const reports = "src/routes/reports.ts";
  const getPriceFile = "src/handlers/getPrice.ts";
  const recalcFile = "src/handlers/recalculate.ts";
  const client = "src/clients/forecastClient.ts";
  const gen = "src/generated/salesApi.ts";
  const dbFile = "src/db/priceRepository.ts";
  const listener = "src/messaging/statsListener.ts";
  const ratioFile = "src/pricing/demandRatio.ts";
  const priceFile = priceForDemandModule(tag);
  const legacy = "src/legacy/revenue.js";
  const config = "config/default.json";
  const priceForDemand = k.priceForDemand(tag);

  a.node({ key: k.proj, kind: "Project", name: PKG, at: ["package.json", '"name": "@fixture/pricing"'] });
  a.node({ key: k.service, kind: "Service", name: PKG, props: { framework: "express" }, at: ["package.json", '"express": "5.3.0"'] });
  a.node({ key: k.serverModule, kind: "File", name: "src/server.ts", props: { topLevelCode: true }, at: [server, "const app = express();"] });
  a.node({ key: k.reportsModule, kind: "File", name: "src/routes/reports.ts", props: { topLevelCode: true }, at: [reports, "export const reportRouter = Router();"] });
  a.node({
    key: k.getEndpoint, kind: "Endpoint", name: "GET /api/pricing/:articleId",
    props: { method: "GET", route: "/api/pricing/:articleId", normalizedRoute: "/api/pricing/{}", style: "express-router", mountPath: "/api/pricing" },
    at: [[server, 'app.use("/api/pricing", pricingRouter)'], [routes, 'router.get("/:articleId", getPrice)']],
  });
  a.node({
    key: k.recalcEndpoint, kind: "Endpoint", name: "POST /api/pricing/:articleId/recalculate",
    props: { method: "POST", route: "/api/pricing/:articleId/recalculate", normalizedRoute: "/api/pricing/{}/recalculate", style: "express-router", mountPath: "/api/pricing" },
    at: [[server, 'app.use("/api/pricing", pricingRouter)'], [routes, 'router.post("/:articleId/recalculate", recalculate)']],
  });
  a.node({
    key: k.reportEndpoint, kind: "Endpoint", name: "GET /api/pricing/reports/prices",
    props: { method: "GET", route: "/api/pricing/reports/prices", normalizedRoute: "/api/pricing/reports/prices", style: "express-router", mountPath: "/api/pricing/reports" },
    at: [[server, 'app.use("/api/pricing/reports", reportRouter)'], [reports, 'reportRouter.get("/prices"']],
  });
  a.node({ key: k.getPrice, kind: "Function", name: "getPrice", props: { exported: true, async: true }, at: [getPriceFile, "export async function getPrice("] });
  a.node({ key: k.recalculate, kind: "Function", name: "recalculate", props: { exported: true, async: true }, at: [recalcFile, "export async function recalculate("] });
  a.node({ key: k.getForecast, kind: "Function", name: "getForecast", props: { exported: true, async: true }, at: [client, "export async function getForecast("] });
  a.node({ key: k.forecastResponse, kind: "Type", name: "ForecastResponse", props: { typeKind: "interface", exported: true }, at: [client, "export interface ForecastResponse"] });
  a.node({
    key: k.salesApi, kind: "Type", name: "SalesApi", generated: true,
    props: { typeKind: "class", exported: true, generator: "openapi-client-gen 3.4" },
    at: [[gen, "// This file was auto-generated by openapi-client-gen 3.4"], [gen, "export class SalesApi"]],
  });
  a.node({ key: k.getWeeklySales, kind: "Method", name: "getWeeklySales", generated: true, props: { operationId: "Articles_GetWeeklySales" }, at: [gen, "getWeeklySales(articleId: string, from: string, to: string)"] });
  a.node({ key: k.request, kind: "Method", name: "request", generated: true, props: { accessibility: "private" }, at: [gen, "private async request<T>("] });
  a.node({ key: k.findPrice, kind: "Function", name: "findPrice", props: { exported: true, async: true }, at: [dbFile, "export async function findPrice("] });
  a.node({ key: k.listPrices, kind: "Function", name: "listPrices", props: { exported: true, async: true }, at: [dbFile, "export async function listPrices("] });
  a.node({ key: k.saveSignal, kind: "Function", name: "saveSignal", props: { exported: true, async: true }, at: [dbFile, "export async function saveSignal("] });
  a.node({ key: k.onStatsUpdated, kind: "Function", name: "onStatsUpdated", props: { exported: false, async: true }, at: [listener, "async function onStatsUpdated("] });
  a.node({ key: k.startListener, kind: "Function", name: "startStatsListener", props: { exported: true, async: true }, at: [listener, "export async function startStatsListener("] });
  // Overloads are one TypeScript symbol with several declarations.
  a.node({
    key: k.demandRatio, kind: "Function", name: "demandRatio",
    props: { exported: true, overloads: ["(forecastPerWeek: number, capacityPerWeek: number): number", "(forecast: ForecastResponse, capacityPerWeek: number): number"] },
    at: [
      [ratioFile, "export function demandRatio(forecastPerWeek: number, capacityPerWeek: number): number;"],
      [ratioFile, "export function demandRatio(forecast: ForecastResponse, capacityPerWeek: number): number;"],
      [ratioFile, "export function demandRatio(forecast: number | ForecastResponse, capacityPerWeek: number): number {"],
    ],
  });
  a.node({
    key: priceForDemand, kind: "Function", name: "priceForDemand",
    props: tag === "v1-base" ? { exported: true } : { exported: true, reExportedFrom: ["src/pricing/priceForDemand.ts"] },
    at: tag === "v1-base"
      ? [priceFile, "export function priceForDemand("]
      : [[priceFile, "export function priceForDemand("], ["src/pricing/priceForDemand.ts", 'export { priceForDemand } from "./demand.js";']],
  });
  a.node({ key: k.weeklyRevenueSek, kind: "Function", name: "weeklyRevenueSek", props: { exported: true, language: "javascript", typedBy: "jsdoc" }, at: [legacy, "export function weeklyRevenueSek(rows, sekPerUnit, weeks)"] });
  a.node({
    key: k.queue, kind: "Channel", name: "article-stats-updated",
    props: { provider: "azure-servicebus", entityType: "queue", configSource: config },
    at: [[config, '"fullyQualifiedNamespace": "sb-pricing.servicebus.windows.net"'], [config, '"queue": "article-stats-updated"']],
  });

  // ---- architecture ----
  for (const [ep, mount] of [[k.getEndpoint, 'app.use("/api/pricing", pricingRouter)'], [k.recalcEndpoint, 'app.use("/api/pricing", pricingRouter)'], [k.reportEndpoint, 'app.use("/api/pricing/reports", reportRouter)']]) {
    a.edge({ from: k.service, rel: "EXPOSES", to: ep, derivation: "rule", rule: EXPRESS, at: [server, mount] });
  }
  a.edge({ from: k.getPrice, rel: "HANDLES", to: k.getEndpoint, derivation: "rule", rule: EXPRESS, at: [routes, 'router.get("/:articleId", getPrice)'] });
  a.edge({ from: k.recalculate, rel: "HANDLES", to: k.recalcEndpoint, derivation: "rule", rule: EXPRESS, at: [routes, 'router.post("/:articleId/recalculate", recalculate)'] });
  // Top-level lambda handler: attributed to the module node.
  a.edge({ from: k.reportsModule, rel: "HANDLES", to: k.reportEndpoint, derivation: "rule", rule: EXPRESS, props: { handler: "lambda" }, at: [reports, "async (req: Request, res: Response) =>"] });
  a.edge({
    from: k.getForecast, rel: "REQUESTS", to: k.forecastEndpoint, derivation: "rule", rule: AXIOS,
    resolution: "unresolved", reason: "configuration",
    props: { method: "GET", normalizedRoute: "/api/forecasts/{}", query: ["strategy"], baseUrlEnv: "FORECAST_API_URL", responseType: k.forecastResponse },
    at: [[client, "http.get<ForecastResponse>(`/api/forecasts/${articleId}`, { params: { strategy } })"], [client, "axios.create({ baseURL: process.env.FORECAST_API_URL"]],
  });
  a.edge({
    from: k.getWeeklySales, rel: "REQUESTS", to: k.salesEndpoint, derivation: "rule", rule: OPENAPI,
    resolution: "unresolved", reason: "configuration",
    props: { method: "GET", normalizedRoute: "/api/articles/{}/weekly-sales", operationId: "Articles_GetWeeklySales", baseUrlEnv: "SALES_API_URL", defaultBaseUrl: "http://sales-api", generated: true },
    at: [[gen, '"/api/articles/{articleId}/weekly-sales"'], [recalcFile, 'new SalesApi(process.env.SALES_API_URL ?? "http://sales-api")']],
  });
  // Inside the generated client: the path is a parameter, so a plain fetch rule cannot bind it.
  a.edge({
    from: k.request, rel: "REQUESTS", derivation: "rule", rule: FETCH,
    resolution: "unresolved", reason: "dynamic-string",
    props: { method: "dynamic", generated: true },
    at: [gen, "fetch(url, { method })"],
  });
  a.edge({ from: k.getWeeklySales, rel: "CALLS", to: k.request, at: [gen, "this.request<WeeklySalesResponse>("] });
  a.edge({
    from: k.findPrice, rel: "READS", to: k.prices, derivation: "rule", rule: PG, resolution: "unresolved", reason: "configuration",
    props: { columns: ["article_id", "amount_ore", "currency"], sqlConstant: "SELECT_PRICE", sqlAnalysis: "full" },
    at: [[dbFile, '"SELECT article_id, amount_ore, currency FROM pricing.prices WHERE article_id = $1"'], [dbFile, "process.env.PRICING_DATABASE_URL"]],
  });
  // Dynamic SQL: the table is known, the ORDER BY column is interpolated.
  a.edge({
    from: k.listPrices, rel: "READS", to: k.prices, derivation: "rule", rule: PG, resolution: "unresolved", reason: "configuration",
    props: { columns: ["article_id", "amount_ore", "currency"], sqlAnalysis: "partial", dynamicParts: ["ORDER BY ${sortColumn}"], allowedValues: ["article_id", "amount_ore"] },
    at: [[dbFile, "`SELECT article_id, amount_ore, currency FROM pricing.prices ORDER BY ${sortColumn}`"], [dbFile, 'sortColumn: "article_id" | "amount_ore"']],
  });
  a.edge({
    from: k.saveSignal, rel: "WRITES", to: k.signals, derivation: "rule", rule: PG, resolution: "unresolved", reason: "configuration",
    props: { columns: ["article_id", "average_per_week", "received_at"], sqlConstant: "INSERT_SIGNAL", sqlAnalysis: "full" },
    at: [dbFile, '"INSERT INTO pricing.demand_signals (article_id, average_per_week, received_at) VALUES ($1, $2, now())"'],
  });
  a.edge({
    from: k.startListener, rel: "CONSUMES", to: k.queue, derivation: "rule", rule: SB,
    props: { configSource: config, credential: "DefaultAzureCredential" },
    at: [[listener, "client.createReceiver(config.serviceBus.queue)"], [listener, "new ServiceBusClient(config.serviceBus.fullyQualifiedNamespace"]],
  });

  // ---- code graph ----
  a.edge({ from: k.serverModule, rel: "CALLS", to: k.startListener, at: [server, "await startStatsListener();"] });
  a.edge({ from: k.reportsModule, rel: "CALLS", to: k.listPrices, at: [reports, "listPrices(sort)"] });
  a.edge({ from: k.getPrice, rel: "CALLS", to: k.findPrice, at: [getPriceFile, "findPrice(req.params.articleId)"] });
  a.edge({ from: k.getPrice, rel: "CALLS", to: k.getForecast, at: [getPriceFile, "getForecast(articleId)"] });
  a.edge({ from: k.getPrice, rel: "CALLS", to: k.demandRatio, props: { overload: 1 }, at: [getPriceFile, "demandRatio(forecast, config.capacityPerWeek)"] });
  // Call site and import are unchanged in v2-move; only the resolved declaration moved.
  a.edge({ from: k.getPrice, rel: "CALLS", to: priceForDemand, at: [getPriceFile, "priceForDemand(price.amount_ore, ratio)"] });
  a.edge({ from: k.recalculate, rel: "CALLS", to: k.getWeeklySales, at: [recalcFile, "salesApi.getWeeklySales(req.params.articleId, from, to)"] });
  a.edge({ from: k.recalculate, rel: "CALLS", to: k.saveSignal, at: [recalcFile, "saveSignal(req.params.articleId, sales.averagePerWeek)"] });
  a.edge({ from: k.onStatsUpdated, rel: "CALLS", to: k.saveSignal, at: [listener, "saveSignal(message.ArticleId, message.AveragePerWeek)"] });
  a.edge({
    from: k.startListener, rel: "CALLS", resolution: "unresolved", reason: "dynamic-dispatch",
    props: { expression: "handlers[String(message.subject)]" },
    at: [listener, "handlers[String(message.subject)]"],
  });
  a.edge({
    from: k.startListener, rel: "CALLS", to: k.onStatsUpdated, derivation: "rule", rule: HANDLER_MAP, resolution: "candidate",
    props: { mapKey: "ArticleStatsUpdated", dispatchExpression: "handlers[String(message.subject)]" },
    at: [[listener, "ArticleStatsUpdated: onStatsUpdated,"], [listener, "handlers[String(message.subject)]"]],
  });
  a.edge({ from: k.demandRatio, rel: "REFERENCES", to: k.forecastResponse, at: [ratioFile, "forecast: ForecastResponse"] });
  a.edge({ from: priceForDemand, rel: "CALLS", to: k.mathTrunc, at: [priceFile, "Math.trunc("] });
  a.edge({ from: priceForDemand, rel: "CALLS", to: k.mathMin, at: [priceFile, "Math.min("] });
  a.edge({ from: priceForDemand, rel: "CALLS", to: k.mathMax, at: [priceFile, "Math.max("] });
  a.edge({ from: k.weeklyRevenueSek, rel: "CALLS", to: k.toFixed, at: [legacy, ".toFixed(2)"] });

  // ---- calculations ----
  const adjustment = ir.bin(
    "*",
    ir.call(k.mathMin, [ir.call(k.mathMax, [ir.bin("-", ir.v("$1"), ir.lit(1, "float64"), "float64"), ir.lit(-0.2, "float64")]), ir.lit(0.3, "float64")], "float64"),
    ir.lit(0.5, "float64"),
    "float64",
  );
  a.calc({
    key: priceForDemand,
    output: { type: "float64", unit: { status: "known", value: "ore", at: [priceFile, "baseOre: number"] } },
    inputs: [
      { ref: "$0", type: "number", unit: { status: "known", value: "ore", at: [priceFile, "baseOre: number"] } },
      { ref: "$1", type: "number" },
    ],
    expr: ir.round("truncate", ir.bin("*", ir.v("$0"), ir.bin("+", ir.lit(1, "float64"), adjustment, "float64"), "float64"), undefined, k.mathTrunc),
    attributes: {
      aggregation: { status: "known", value: "none", at: [priceFile, "export function priceForDemand("] },
      timeBucket: { status: "known", value: "none", at: [priceFile, "export function priceForDemand("] },
      denominatorSource: { status: "known", value: "none", at: [priceFile, "baseOre * (1 + adjustment)"] },
      filters: {
        status: "known", value: "clamp(ratio - 1, -0.2, 0.3) * 0.5",
        detail: { minAdjustment: -0.1, maxAdjustment: 0.15 },
        at: [priceFile, "Math.min(Math.max(ratio - 1, MAX_DISCOUNT), MAX_SURCHARGE) * SENSITIVITY"],
      },
      rounding: { status: "known", value: "truncate", detail: { digits: 0, unit: "ore", api: "Math.trunc" }, at: [priceFile, "Math.trunc("] },
      numericType: { status: "known", value: "float64", detail: { storage: "bigint amount_ore in pricing.prices" }, at: [priceFile, "baseOre: number"] },
      nullHandling: { status: "known", value: "notApplicable", at: [priceFile, "baseOre: number, ratio: number"] },
      zeroDenominator: { status: "known", value: "notApplicable", at: [priceFile, "baseOre * (1 + adjustment)"] },
      currency: { status: "partial", value: "sameAsInput", detail: { note: "price currency is carried alongside by the caller, not used here" }, at: [getPriceFile, "currency: price.currency"] },
    },
    at: [priceFile, "export function priceForDemand("],
  });

  const hasRate = (row) => ir.cmp("!==", ir.index(ir.v("$1"), ir.m(ir.v(row), "currency")), ir.opaque("undefined"));
  const total = ir.agg("sum", ir.v("$0"), {
    bind: "$3", filter: hasRate("$3"), type: "float64",
    value: ir.bin(
      "*",
      ir.bin("*", ir.m(ir.v("$3"), "amountOre", "float64"), ir.m(ir.v("$3"), "quantity", "float64"), "float64"),
      ir.index(ir.v("$1"), ir.m(ir.v("$3"), "currency"), "float64"),
      "float64",
    ),
  });
  a.calc({
    key: k.weeklyRevenueSek,
    output: { type: "float64", unit: { status: "known", value: "SEK", at: [legacy, "Weekly revenue in SEK"] } },
    inputs: [
      { ref: "$0", type: "{ soldAt: string, amountOre: number, quantity: number, currency: string }[]" },
      { ref: "$1", type: "Record<string, number>" },
      { ref: "$2", type: "number" },
    ],
    expr: ir.cond(
      ir.cmp(">", ir.v("$2"), ir.lit(0, "float64")),
      ir.round("halfTowardPositiveInfinity", ir.bin("/", ir.bin("/", total, ir.lit(100, "float64"), "float64"), ir.v("$2"), "float64"), ir.lit(2, "int32"), k.toFixed),
      ir.lit(0, "float64"),
    ),
    attributes: {
      aggregation: { status: "known", value: "sum", at: [legacy, "totalOre += row.amountOre * row.quantity * rate;"] },
      timeBucket: { status: "partial", value: "suppliedByCaller", detail: { note: "rows are not bucketed; weeks is a parameter" }, at: [legacy, "@param {number} weeks"] },
      period: { status: "partial", value: "suppliedByCaller", at: [legacy, "@param {number} weeks"] },
      denominatorSource: { status: "known", value: "parameter", at: [legacy, "totalOre / 100 / weeks"] },
      filters: { status: "known", value: "currency has a known SEK rate", at: [legacy, "if (rate === undefined) continue;"] },
      rounding: {
        status: "known", value: "halfTowardPositiveInfinity",
        detail: { digits: 2, api: "Number.prototype.toFixed", note: "applied to the exact binary value of the double" },
        at: [legacy, ".toFixed(2)"],
      },
      numericType: { status: "known", value: "float64", at: [legacy, "let totalOre = 0;"] },
      nullHandling: { status: "known", value: "notApplicable", at: [legacy, "amountOre: number, quantity: number"] },
      zeroDenominator: { status: "known", value: "returnsZero", at: [legacy, "weeks > 0 ?"] },
      currency: {
        status: "known", value: "convertedToSek",
        detail: { unknownCurrencies: "silently dropped" },
        at: [[legacy, "sekPerUnit[row.currency]"], [legacy, "if (rate === undefined) continue;"]],
      },
    },
    at: [legacy, "export function weeklyRevenueSek(rows, sekPerUnit, weeks)"],
  });
  return a;
}

function database(repo) {
  const a = new Artifact(repo, {
    unitPath: "db", unitName: DBUNIT, language: "sql",
    sourcePrefix: "db", sourceExtensions: [".sql"], rules: [SQLRULE],
  });
  const m1 = "db/migrations/001_pricing.sql";
  const m2 = "db/migrations/002_weekly_revenue.sql";
  const d = k.db;
  a.node({ key: d.proj, kind: "Project", name: DBUNIT, props: { dialect: "postgresql", migrations: true }, at: [m1, "CREATE SCHEMA pricing;"] });
  a.node({ key: d.prices, kind: "Table", name: "pricing.prices", at: [m1, "CREATE TABLE pricing.prices"] });
  a.node({ key: d.ledger, kind: "Table", name: "pricing.sales_ledger", at: [m1, "CREATE TABLE pricing.sales_ledger"] });
  a.node({ key: d.ledgerArticleId, kind: "Column", name: "article_id", signature: "text NOT NULL", at: [m1, "article_id text       NOT NULL"] });
  a.node({ key: d.ledgerSoldAt, kind: "Column", name: "sold_at", signature: "timestamptz NOT NULL", at: [m1, "sold_at    timestamptz NOT NULL"] });
  a.node({ key: d.ledgerAmount, kind: "Column", name: "amount_ore", signature: "bigint NOT NULL", at: [m1, "amount_ore bigint     NOT NULL", 2] });
  a.node({ key: d.ledgerQuantity, kind: "Column", name: "quantity", signature: "integer NOT NULL", at: [m1, "quantity   integer    NOT NULL"] });
  a.node({ key: d.ledgerCurrency, kind: "Column", name: "currency", signature: "char(3) NOT NULL", at: [m1, "currency   char(3)    NOT NULL", 2] });
  a.node({ key: d.signals, kind: "Table", name: "pricing.demand_signals", at: [m1, "CREATE TABLE pricing.demand_signals"] });
  a.node({ key: d.weeklyRevenue, kind: "Routine", name: "pricing.weekly_revenue", props: { routineKind: "sql-function", volatility: "STABLE" }, at: [m2, "CREATE FUNCTION pricing.weekly_revenue"] });

  for (const [t, at] of [[d.prices, [m1, "CREATE TABLE pricing.prices"]], [d.ledger, [m1, "CREATE TABLE pricing.sales_ledger"]], [d.signals, [m1, "CREATE TABLE pricing.demand_signals"]], [d.weeklyRevenue, [m2, "CREATE FUNCTION pricing.weekly_revenue"]]]) {
    a.edge({ from: d.proj, rel: "DECLARES", to: t, derivation: "rule", rule: SQLRULE, at });
  }
  for (const [c, at] of [[d.ledgerArticleId, [m1, "article_id text       NOT NULL"]], [d.ledgerSoldAt, [m1, "sold_at    timestamptz NOT NULL"]], [d.ledgerAmount, [m1, "amount_ore bigint     NOT NULL", 2]], [d.ledgerQuantity, [m1, "quantity   integer    NOT NULL"]], [d.ledgerCurrency, [m1, "currency   char(3)    NOT NULL", 2]]]) {
    a.edge({ from: d.ledger, rel: "DECLARES", to: c, derivation: "rule", rule: SQLRULE, at });
  }
  a.edge({ from: d.weeklyRevenue, rel: "READS", to: d.ledger, derivation: "rule", rule: SQLRULE, at: [m2, "FROM pricing.sales_ledger AS s"] });
  a.edge({ from: d.weeklyRevenue, rel: "READS", to: d.ledgerAmount, derivation: "rule", rule: SQLRULE, at: [m2, "s.amount_ore"] });
  a.edge({ from: d.weeklyRevenue, rel: "READS", to: d.ledgerQuantity, derivation: "rule", rule: SQLRULE, at: [m2, "s.quantity"] });
  a.edge({ from: d.weeklyRevenue, rel: "READS", to: d.ledgerArticleId, derivation: "rule", rule: SQLRULE, at: [m2, "s.article_id = p_article_id"] });
  a.edge({ from: d.weeklyRevenue, rel: "READS", to: d.ledgerSoldAt, derivation: "rule", rule: SQLRULE, at: [m2, "date_trunc('week', s.sold_at)"] });

  const inPeriod = (row) => ir.and(
    ir.cmp("==", ir.m(ir.v(row), "article_id"), ir.v("$0")),
    ir.cmp(">=", ir.m(ir.v(row), "sold_at"), ir.v("$1")),
    ir.cmp("<", ir.m(ir.v(row), "sold_at"), ir.bin("+", ir.v("$2"), ir.lit(1, "int32"))),
  );
  a.calc({
    key: d.weeklyRevenue,
    output: { type: "numeric", unit: { status: "unknown" } },
    inputs: [
      { ref: "$0", type: "text" },
      { ref: "$1", type: "date" },
      { ref: "$2", type: "date" },
      { ref: "$3", type: "table", origin: d.ledger },
    ],
    expr: ir.round(
      "halfAwayFromZero",
      ir.bin(
        "/",
        ir.bin(
          "/",
          ir.agg("sum", ir.v("$3"), {
            bind: "$4", filter: inPeriod("$4"), type: "numeric",
            value: ir.bin("*", ir.m(ir.v("$4"), "amount_ore", "bigint"), ir.m(ir.v("$4"), "quantity", "int32"), "bigint"),
          }),
          ir.lit("100.0", "numeric"),
          "numeric",
        ),
        ir.call(d.nullif, [
          ir.agg("countDistinct", ir.v("$3"), { bind: "$5", filter: inPeriod("$5"), value: ir.call(d.dateTrunc, [ir.lit("week"), ir.m(ir.v("$5"), "sold_at")]), type: "bigint" }),
          ir.lit(0, "int32"),
        ], "bigint"),
        "numeric",
      ),
      ir.lit(2, "int32"),
      d.round,
    ),
    attributes: {
      aggregation: { status: "known", value: "sum", at: [m2, "SUM(s.amount_ore * s.quantity)"] },
      timeBucket: {
        status: "known", value: "isoWeek",
        detail: { function: "date_trunc('week', timestamptz)", note: "Monday-based weeks in the session TimeZone" },
        at: [m2, "date_trunc('week', s.sold_at)"],
      },
      period: { status: "known", value: "requested", detail: { bounds: "[p_from, p_to + 1) half-open" }, at: [m2, "s.sold_at < p_to + 1"] },
      denominatorSource: { status: "known", value: "data", detail: { description: "distinct weeks with at least one ledger row" }, at: [m2, "COUNT(DISTINCT date_trunc('week', s.sold_at))"] },
      filters: { status: "known", value: "article_id = p_article_id and sold_at in [p_from, p_to+1)", at: [m2, "WHERE s.article_id = p_article_id"] },
      rounding: { status: "known", value: "halfAwayFromZero", detail: { digits: 2, api: "PostgreSQL ROUND(numeric, int)" }, at: [m2, "ROUND("] },
      numericType: { status: "known", value: "numeric", detail: { inputs: "bigint * integer, divided by numeric 100.0" }, at: [m2, "RETURNS numeric"] },
      nullHandling: { status: "known", value: "notApplicable", detail: { reason: "all inputs are NOT NULL columns" }, at: [m1, "amount_ore bigint     NOT NULL", 2] },
      zeroDenominator: { status: "known", value: "returnsNull", at: [m2, "NULLIF(COUNT(DISTINCT date_trunc('week', s.sold_at)), 0)"] },
      // Names and scaling do not establish a currency domain or monetary unit.
      currency: { status: "unknown" },
      overflow: { status: "known", value: "throws", detail: { expression: "bigint * integer", error: "bigint out of range" }, at: [m2, "s.amount_ore * s.quantity"] },
    },
    at: [m2, "CREATE FUNCTION pricing.weekly_revenue"],
  });
  return a;
}

export default function pricingNode(repo, tag) {
  return [service(repo, tag), database(repo)];
}
