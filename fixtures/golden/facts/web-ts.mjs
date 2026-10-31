import { Artifact, ir, key } from "../lib.mjs";

const R = "web-ts";
const PKG = "@fixture/web";
const FETCH = { name: "web.fetch", version: "1" };

const ts = (mod, decl, meaning) => key.ts(R, PKG, mod, decl, meaning);
export const k = {
  proj: key.proj(R, "tsconfig.json"),
  baseUrl: ts("src/config.ts", "salesApiBaseUrl"),
  fetchWeeklySales: ts("src/api/salesClient.ts", "fetchWeeklySales"),
  response: ts("src/api/salesClient.ts", "WeeklySalesResponse", "type"),
  isoWeekKey: ts("src/sales/isoWeek.ts", "isoWeekKey"),
  average: ts("src/sales/weeklyStats.ts", "averagePerActiveWeek"),
  draftSale: ts("src/sales/weeklyStats.ts", "DraftSale", "type"),
  render: ts("src/views/articleSummary.ts", "renderArticleSummary"),
  format: ts("src/views/articleSummary.ts", "formatQuantity"),
  fetch: key.tsExt("typescript", "lib.dom.d.ts", "fetch"),
  mathRound: key.tsExt("typescript", "lib.es5.d.ts", "Math.round"),
  // Base URL comes from runtime configuration, so the target service is not bound yet.
  weeklyEndpoint: key.http("@unbound:base=salesApiBaseUrl", "GET", "/api/articles/{}/weekly-sales"),
};

export default function webTs(repo) {
  const a = new Artifact(repo, {
    unitPath: "tsconfig.json", unitName: PKG, language: "typescript",
    sourcePrefix: "src", sourceExtensions: [".ts"], inputPaths: ["src", "package.json", "package-lock.json", "tsconfig.json"], rules: [FETCH],
  });
  const cfg = "src/config.ts";
  const client = "src/api/salesClient.ts";
  const iso = "src/sales/isoWeek.ts";
  const stats = "src/sales/weeklyStats.ts";
  const view = "src/views/articleSummary.ts";

  a.node({ key: k.proj, kind: "Project", name: PKG, at: ["package.json", '"name": "@fixture/web"'] });
  a.node({ key: k.baseUrl, kind: "Variable", name: "salesApiBaseUrl", props: { exported: true }, at: [cfg, "export const salesApiBaseUrl"] });
  a.node({ key: k.fetchWeeklySales, kind: "Function", name: "fetchWeeklySales", props: { exported: true, async: true }, at: [client, "export async function fetchWeeklySales("] });
  a.node({ key: k.response, kind: "Type", name: "WeeklySalesResponse", props: { typeKind: "interface", exported: true }, at: [client, "export interface WeeklySalesResponse"] });
  a.node({ key: k.isoWeekKey, kind: "Function", name: "isoWeekKey", props: { exported: true }, at: [iso, "export function isoWeekKey("] });
  a.node({ key: k.average, kind: "Function", name: "averagePerActiveWeek", props: { exported: true }, at: [stats, "export function averagePerActiveWeek("] });
  a.node({ key: k.draftSale, kind: "Type", name: "DraftSale", props: { typeKind: "interface", exported: true }, at: [stats, "export interface DraftSale"] });
  a.node({ key: k.render, kind: "Function", name: "renderArticleSummary", props: { exported: true, async: true }, at: [view, "export async function renderArticleSummary("] });
  a.node({ key: k.format, kind: "Function", name: "formatQuantity", props: { exported: false }, at: [view, "function formatQuantity("] });

  a.edge({ from: k.fetchWeeklySales, rel: "REFERENCES", to: k.baseUrl, at: [client, "${salesApiBaseUrl}"] });
  a.edge({ from: k.fetchWeeklySales, rel: "CALLS", to: k.fetch, at: [client, 'fetch(url, { method: "GET" })'] });
  a.edge({
    from: k.fetchWeeklySales, rel: "REQUESTS", to: k.weeklyEndpoint, derivation: "rule", rule: FETCH,
    resolution: "unresolved", reason: "configuration",
    props: { method: "GET", normalizedRoute: "/api/articles/{}/weekly-sales", baseUrl: k.baseUrl, query: ["from", "to"], responseType: k.response },
    at: [[client, "`${salesApiBaseUrl}/api/articles/${encodeURIComponent(articleId)}/weekly-sales?from=${from}&to=${to}`"], [client, 'fetch(url, { method: "GET" })']],
  });
  a.edge({ from: k.render, rel: "CALLS", to: k.fetchWeeklySales, at: [view, "fetchWeeklySales(articleId, from, to)"] });
  a.edge({ from: k.render, rel: "CALLS", to: k.average, at: [view, "averagePerActiveWeek(drafts)"] });
  a.edge({ from: k.render, rel: "CALLS", to: k.format, at: [view, "formatQuantity(server.averagePerWeek)"] });
  a.edge({ from: k.average, rel: "CALLS", to: k.isoWeekKey, at: [stats, "isoWeekKey(r.soldOn)"] });
  a.edge({ from: k.average, rel: "CALLS", to: k.mathRound, at: [stats, "Math.round((total / weeks.size) * 100)"] });

  const notNull = (r) => ir.cmp("!==", ir.m(ir.v(r), "quantity"), ir.lit(null));
  const weeks = ir.agg("countDistinct", ir.v("$0"), {
    bind: "$1", filter: notNull("$1"), value: ir.call(k.isoWeekKey, [ir.m(ir.v("$1"), "soldOn")]), type: "float64",
  });
  const total = ir.agg("sum", ir.v("$0"), { bind: "$2", filter: notNull("$2"), value: ir.m(ir.v("$2"), "quantity", "float64"), type: "float64" });
  a.calc({
    key: k.average,
    includes: [k.isoWeekKey],
    output: { type: "float64", unit: { status: "unknown" } },
    inputs: [{ ref: "$0", type: "readonly DraftSale[]", origin: k.draftSale }],
    expr: ir.cond(
      ir.cmp("===", weeks, ir.lit(0, "float64")),
      ir.lit(0, "float64"),
      ir.bin(
        "/",
        ir.round("halfTowardPositiveInfinity", ir.bin("*", ir.bin("/", total, weeks, "float64"), ir.lit(100, "float64"), "float64"), undefined, k.mathRound),
        ir.lit(100, "float64"),
        "float64",
      ),
    ),
    attributes: {
      aggregation: { status: "known", value: "sum", at: [stats, "counted.reduce((sum, r) => sum + (r.quantity as number), 0)"] },
      timeBucket: { status: "known", value: "isoWeek", detail: { source: "hand-written isoWeekKey (UTC)" }, at: [iso, "export function isoWeekKey("] },
      period: { status: "known", value: "none", detail: { description: "no period filter; all supplied draft rows count" }, at: [stats, "rows.filter((r) => r.quantity !== null)"] },
      denominatorSource: { status: "known", value: "data", detail: { description: "distinct ISO weeks among rows with quantity !== null" }, at: [stats, "new Set(counted.map((r) => isoWeekKey(r.soldOn)))"] },
      filters: { status: "known", value: "quantity !== null", at: [stats, "rows.filter((r) => r.quantity !== null)"] },
      rounding: {
        status: "known", value: "halfTowardPositiveInfinity",
        detail: { digits: 2, method: "Math.round(x * 100) / 100", note: "binary floating point: scaled value may not be an exact midpoint" },
        at: [stats, "Math.round((total / weeks.size) * 100) / 100"],
      },
      numericType: { status: "known", value: "float64", at: [stats, "quantity: number | null"] },
      nullHandling: { status: "known", value: "excluded", detail: { note: "strict !== null; undefined would be counted" }, at: [stats, "r.quantity !== null"] },
      zeroDenominator: { status: "known", value: "returnsZero", at: [stats, "return 0;"] },
      currency: { status: "unknown" },
    },
    at: [stats, "export function averagePerActiveWeek("],
  });
  return [a];
}
