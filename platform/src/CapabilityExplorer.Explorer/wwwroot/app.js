import { h, clear, pill, resolutionPill, statusPill, kindPill, shortSha, symbolHref, symbolLink, table, kv, propsList, sourceView, evidenceLinks, ratioBar } from "./ui.js";
import { formula, shortTarget } from "./ir.js";

const main = document.getElementById("main");
const nav = document.getElementById("nav");
const revbar = document.getElementById("revisions");

const VIEWS = [
  ["explore", "Explore"],
  ["integrations", "Integrations"],
  ["calculations", "Calculations"],
  ["changes", "Changes"],
  ["artifacts", "Artifacts"],
  ["evaluation", "Evaluation"],
];
const ATTRIBUTES = ["aggregation", "timeBucket", "period", "denominatorSource", "filters", "rounding", "numericType", "nullHandling", "zeroDenominator", "currency", "overflow"];

let meta;
let selection = {};

async function api(path) {
  const res = await fetch(path, { headers: { accept: "application/json" } });
  const body = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(body.error ?? `${res.status} ${res.statusText}`);
  return body;
}

const viewSpec = () => Object.entries(selection).map(([r, s]) => `${r}:${s}`).join(",");
const withView = (path) => `${path}${path.includes("?") ? "&" : "?"}view=${encodeURIComponent(viewSpec())}`;
const revisionsOf = (repo) => meta.revisions.filter((r) => r.repositoryId === repo);
const revisionLabel = (repo, sha) => meta.revisions.find((r) => r.repositoryId === repo && r.commitSha === sha)?.label ?? shortSha(sha);

function loadSelection() {
  selection = { ...meta.defaultView };
  try {
    const saved = JSON.parse(localStorage.getItem("ce.selection") ?? "{}");
    for (const [repo, sha] of Object.entries(saved)) if (revisionsOf(repo).some((r) => r.commitSha === sha)) selection[repo] = sha;
  } catch { /* storage unavailable: defaults */ }
}

function saveSelection() {
  try { localStorage.setItem("ce.selection", JSON.stringify(selection)); } catch { /* ignore */ }
}

function renderRevisionBar() {
  clear(revbar,
    h("span", { class: "label" }, "Revisions"),
    Object.keys(meta.defaultView).sort().map((repo) => h("label", {},
      h("span", {}, repo),
      h("select", {
        "aria-label": `Revision of ${repo}`,
        onchange: (e) => { selection[repo] = e.target.value; saveSelection(); route(); },
      }, revisionsOf(repo).map((r) => h("option", { value: r.commitSha, selected: selection[repo] === r.commitSha }, `${r.label}${r.commitSha === meta.defaultView[repo] ? " (latest)" : ""}`))))),
    h("span", { class: "hint" }, "A chosen combination of code revisions — not a deployment record."));
}

function renderNav(current) {
  clear(nav, VIEWS.map(([id, label]) => h("a", { href: `#/${id}`, class: id === current ? "active" : undefined }, label)));
}

function parseHash() {
  const [path, query] = location.hash.replace(/^#\/?/, "").split("?");
  return { view: path || "explore", params: new URLSearchParams(query ?? "") };
}

async function route() {
  const { view, params } = parseHash();
  renderNav(view);
  renderRevisionBar();
  const render = { explore, integrations, calculations, changes, artifacts, evaluation }[view] ?? explore;
  try {
    await render(params);
  } catch (e) {
    clear(main, h("div", { class: "card error" }, e.message));
  }
}

// ---------------------------------------------------------------- Explore

let lastSearch = { q: "", kind: "", repo: "" };

async function explore(params) {
  const key = params.get("key");
  const results = h("div", { class: "results" });
  const detail = h("div", {});
  const q = h("input", { type: "search", placeholder: "Search symbols, e.g. average, Forecast, dbo.Sales", value: lastSearch.q, "aria-label": "Search symbols" });
  const kind = h("select", { class: "input", "aria-label": "Kind" }, h("option", { value: "" }, "All kinds"), meta.nodeKinds.map((k) => h("option", { value: k, selected: lastSearch.kind === k }, k)));
  const repo = h("select", { class: "input", "aria-label": "Repository" }, h("option", { value: "" }, "All repositories"), Object.keys(meta.defaultView).sort().map((r) => h("option", { value: r, selected: lastSearch.repo === r }, r)));

  let timer;
  const search = async () => {
    lastSearch = { q: q.value, kind: kind.value, repo: repo.value };
    const hits = await api(withView(`/api/search?q=${encodeURIComponent(q.value)}&kind=${kind.value}&repo=${repo.value}`));
    clear(results, hits.length === 0 ? h("div", { class: "empty" }, "No symbols match.") : hits.map((hit) => h("a", { href: symbolHref(hit.key), class: hit.key === key ? "active" : undefined },
      h("div", { class: "name" }, hit.name),
      h("div", { class: "sub" }, kindPill(hit.kind), hit.repositoryId, hit.hasCalculation ? pill("calculation", "info") : null, hit.generated ? pill("generated", "warn") : null))));
  };
  q.addEventListener("input", () => { clearTimeout(timer); timer = setTimeout(search, 120); });
  kind.addEventListener("change", search);
  repo.addEventListener("change", search);

  clear(main, h("div", { class: "split" },
    h("aside", { class: "sidebar" }, h("div", { class: "controls" }, q, h("div", { class: "row" }, kind, repo)), results),
    detail));
  await search();
  if (key) await symbolDetail(detail, key);
  else clear(detail, h("div", { class: "card" },
    h("h1", {}, "Explore the extracted model"),
    h("p", { class: "muted" }, "Search for a symbol on the left. Every fact links to the exact source span at the revision it was observed in."),
    h("div", { class: "grid" },
      quickStart("Weekly averages", "average", "Four implementations of “sales per week” that differ in denominator, rounding and null handling."),
      quickStart("Endpoints", "", "Every HTTP endpoint exposed in the selected revisions.", "Endpoint"),
      quickStart("Channels", "", "Service Bus topics and queues, bound and unbound.", "Channel"))));
  if (!key) q.focus();
}

function quickStart(title, query, text, kindValue = "") {
  return h("a", { class: "card", href: "#/explore", onclick: (e) => { e.preventDefault(); lastSearch = { q: query, kind: kindValue, repo: "" }; route(); } },
    h("div", { style: "font-weight:600" }, title), h("div", { class: "muted small" }, text));
}

async function symbolDetail(container, key) {
  clear(container, h("div", { class: "card muted" }, "Loading…"));
  let d;
  try {
    d = await api(withView(`/api/symbol?key=${encodeURIComponent(key)}`));
  } catch (e) {
    clear(container, h("div", { class: "card" }, h("p", {}, e.message), h("div", { class: "key" }, key), h("p", { class: "muted small" }, "It may exist in another revision: change the revision selection above.")));
    return;
  }
  const n = d.node;
  const sources = h("div", {});
  for (const ev of n.evidence) sources.append(sourceView(api, ev, { open: true }).el);

  clear(container,
    h("div", { class: "card" },
      h("div", { class: "symbol-head" }, h("h1", {}, n.name), kindPill(n.kind), n.generated ? pill("generated", "warn", "Generated code: not counted as an independent implementation") : null),
      h("div", { class: "key" }, n.key),
      h("div", { style: "margin-top:10px" }, kv([
        ["Repository", `${n.repositoryId} @ ${revisionLabel(n.repositoryId, n.commitSha)} (${shortSha(n.commitSha)})`],
        ["Unit", n.unit],
        ["Signature", n.signature ? h("span", { class: "mono" }, n.signature) : null],
      ])),
      n.props ? h("div", {}, h("h3", {}, "Properties"), propsList(n.props)) : null,
      h("h3", {}, "Across revisions"),
      h("div", { class: "history" }, d.history.map((x) => h("span", { class: `${x.state}${x.commitSha === n.commitSha ? " current" : ""}`, title: `${x.subject ?? ""} — ${x.state}` }, h("i"), x.label, h("span", { class: "muted" }, stateLabel(x.state))))),
      h("h3", {}, "Evidence"), sources),
    d.calculation ? calculationCard(d.calculation) : null,
    edgeSection("Outgoing", d.outgoing, "to"),
    edgeSection("Incoming", d.incoming, "from"));
}

const stateLabel = (s) => ({ present: "present", absent: "absent", notObserved: "not observed (partial analysis)", notAnalysed: "unit not analysed" }[s] ?? s);

function edgeSection(title, edges, side) {
  const rows = [...edges].sort((a, b) => a.relation.localeCompare(b.relation)).map((e) => [
    h("span", { class: "mono small" }, e.relation),
    side === "to" ? (e.to ? symbolLink(e.to) : h("span", { class: "muted" }, "unknown target")) : symbolLink(e.from),
    resolutionPill(e.resolution, e.unresolvedReason),
    h("span", { class: "small" }, e.derivation, e.rule ? h("span", { class: "muted" }, ` · ${e.rule}`) : null),
    e.props ? h("details", {}, h("summary", { class: "small muted" }, "props"), propsList(e.props)) : null,
    evidenceLinks(api, e.evidence),
  ]);
  return h("div", { class: "card" }, h("h2", { style: "margin-top:0" }, `${title} (${edges.length})`),
    table(["Relation", side === "to" ? "Target" : "Source", "Resolution", "Derived by", "", "Evidence"], rows));
}

function attributeCell(attr) {
  if (!attr) return h("span", { class: "attr-val unknown" }, "—");
  if (attr.status === "unknown") return h("span", { class: "attr-val unknown", title: "No evidence in the code; not inferred from names" }, "unknown");
  return h("span", { class: `attr-val ${attr.status}`, title: attr.detail ? JSON.stringify(attr.detail) : attr.status }, String(attr.value ?? attr.status));
}

function calculationCard(c) {
  const rows = ATTRIBUTES.filter((a) => c.attributes[a]).map((a) => {
    const attr = c.attributes[a];
    return [a, h("span", {}, attributeCell(attr), attr.status === "partial" ? pill("partial", "warn") : null),
      attr.detail ? h("span", { class: "small muted" }, Object.entries(attr.detail).map(([k, v]) => `${k}: ${typeof v === "string" && v.includes("|") ? shortTarget(v) : v}`).join(" · ")) : "",
      evidenceLinks(api, c.attributeEvidence[a])];
  });
  return h("div", { class: "card" },
    h("div", { class: "row" }, h("h2", { style: "margin:0" }, "Calculation"), h("span", { class: "spacer" }), h("span", { class: "mono small muted", title: "sha256 of the normalised expression" }, c.irHash ? `irHash ${c.irHash.slice(0, 12)}` : "")),
    h("h3", {}, "Normalised expression"), formula(c.expr, c.inputs),
    c.includes.length ? h("p", { class: "small muted" }, "Includes helpers: ", c.includes.map(shortTarget).join(", ")) : null,
    h("h3", {}, "Attributes"),
    table(["Attribute", "Value", "Detail", "Evidence"], rows));
}

// ---------------------------------------------------------------- Integrations

async function integrations() {
  clear(main, h("div", { class: "card muted" }, "Loading…"));
  const d = await api(withView("/api/integrations"));
  const matchTone = { bound: "ok", route: "warn", none: "bad", dynamic: "" };

  clear(main,
    h("h1", {}, "How services connect"),
    h("p", { class: "muted" }, "Derived from rule-based extraction. A cross-repo match by route or name is a candidate, not a proven connection: base URLs, namespaces and connection strings stay unbound until configuration is analysed."),
    h("h2", {}, "Services and endpoints"),
    h("div", { class: "grid" }, d.services.map((s) => h("div", { class: "card" },
      h("div", { class: "row" }, h("a", { href: symbolHref(s.service.key), style: "font-weight:650" }, s.service.name), h("span", { class: "muted small" }, `${s.service.repositoryId} @ ${revisionLabel(s.service.repositoryId, s.service.commitSha)}`)),
      s.endpoints.length === 0 ? h("p", { class: "muted small" }, "No endpoints exposed.") :
        h("div", { style: "margin-top:8px" }, s.endpoints.map((ep) => h("div", { style: "padding:4px 0" },
          h("span", { class: "method" }, ep.endpoint.props?.method ?? ""), " ",
          h("a", { href: symbolHref(ep.endpoint.key), class: "mono" }, ep.endpoint.props?.route ?? ep.endpoint.name),
          h("div", { class: "small muted" }, "handled by ", ep.handlers.length ? ep.handlers.map((r, i) => [i ? ", " : "", h("a", { href: symbolHref(r.key) }, r.name)]) : "—"))))))),

    h("h2", {}, "HTTP calls"),
    table(["Client", "Request", "Match", "Candidate endpoint", "Why"], d.http.map((l) => [
      h("span", {}, symbolLink(l.request.from)),
      l.request.to ? h("span", { class: "mono small" }, l.request.to.name) : h("span", { class: "muted" }, "dynamic"),
      pill(l.match, matchTone[l.match]),
      l.candidates.length ? l.candidates.map((c) => h("div", {}, symbolLink(c))) : h("span", { class: "muted" }, "—"),
      h("span", { class: "small muted" }, l.note),
    ])),

    h("h2", {}, "Messaging"),
    d.channels.map((g) => resourceGroup(g)),
    h("h2", {}, "Data"),
    d.data.map((g) => resourceGroup(g)));
}

function resourceGroup(g) {
  return h("div", { class: "card" },
    h("div", { class: "row" }, h("span", { class: "mono", style: "font-weight:650" }, g.name), pill(`${g.keys.length} key${g.keys.length === 1 ? "" : "s"}`)),
    g.note ? h("div", { class: "note" }, g.note) : null,
    g.keys.map((k) => h("div", { style: "margin-top:10px" },
      h("div", { class: "row" }, pill(k.bound ? "bound" : "unbound", k.bound ? "ok" : "warn"), h("span", { class: "key" }, k.key)),
      k.declaration ? h("div", { class: "small" }, "declared by ", h("a", { href: symbolHref(k.declaration.key) }, `${k.declaration.repositoryId} ${k.declaration.unit}`)) : null,
      k.edges.length ? table(["Relation", "By", "Column", "Resolution", "Evidence"], k.edges.map((e) => [
        h("span", { class: "mono small" }, e.relation), symbolLink(e.from),
        e.to?.key.includes("#") ? h("span", { class: "mono small" }, e.to.key.split("#").at(-1)) : h("span", { class: "muted small" }, "(whole object)"),
        resolutionPill(e.resolution, e.unresolvedReason), evidenceLinks(api, e.evidence)])) : h("p", { class: "muted small" }, "No readers or writers in the selected revisions."))));
}

// ---------------------------------------------------------------- Calculations

let calcFilter = "";
let highlight = true;

async function calculations(params) {
  clear(main, h("div", { class: "card muted" }, "Loading…"));
  const all = await api(withView("/api/calculations"));
  const selectedKey = params.get("key");
  const filter = h("input", { type: "search", placeholder: "Filter by name, repository or value", value: calcFilter, "aria-label": "Filter calculations" });
  const toggle = h("label", { class: "small" }, h("input", { type: "checkbox", checked: highlight, onchange: (e) => { highlight = e.target.checked; draw(); } }), " highlight differences");
  const holder = h("div", {});
  const detail = h("div", {});

  const draw = () => {
    calcFilter = filter.value;
    const f = calcFilter.toLowerCase();
    const rows = all.filter((c) => !f || [c.name, c.repositoryId, c.key, ...Object.values(c.attributes).map((a) => String(a.value ?? ""))].some((s) => s.toLowerCase().includes(f)));
    // The most common value in a column stays plain; every other value gets its own tint.
    const palettes = Object.fromEntries(ATTRIBUTES.map((a) => {
      const counts = new Map();
      for (const c of rows) {
        const attr = c.attributes[a];
        if (attr && attr.status !== "unknown" && attr.value !== undefined && attr.value !== null) counts.set(String(attr.value), (counts.get(String(attr.value)) ?? 0) + 1);
      }
      const ordered = [...counts.entries()].sort((x, y) => y[1] - x[1]).map(([v]) => v);
      return [a, Object.fromEntries(ordered.slice(1).map((v, i) => [v, `v${i % 6}`]))];
    }));
    clear(holder, h("div", { class: "table-wrap matrix" }, h("table", {},
      h("thead", {}, h("tr", {}, h("th", {}, "Calculation"), h("th", {}, "Repository"), ATTRIBUTES.map((a) => h("th", {}, a)))),
      h("tbody", {}, rows.map((c) => h("tr", { class: `clickable${c.key === selectedKey ? " selected" : ""}`, onclick: () => { location.hash = `#/calculations?key=${encodeURIComponent(c.key)}`; } },
        h("td", {}, h("div", { style: "font-weight:600" }, c.name), h("div", { class: "small muted" }, `${c.language} · ${c.output.type}`)),
        h("td", { class: "small" }, c.repositoryId, h("div", { class: "muted" }, c.unit)),
        ATTRIBUTES.map((a) => {
          const attr = c.attributes[a];
          const cls = highlight && attr && attr.status !== "unknown" ? palettes[a][String(attr.value)] : undefined;
          return h("td", { class: `attr ${cls ?? ""}` }, attributeCell(attr));
        })))))));
  };
  filter.addEventListener("input", draw);

  clear(main,
    h("h1", {}, "Calculations"),
    h("p", { class: "muted" }, "Each row is one implementation, kept separate even when they look like the same business concept. Differences are what the code says; “unknown” means there is no evidence, not that a default applies."),
    h("div", { class: "row", style: "margin-bottom:10px" }, h("div", { style: "flex:1;max-width:420px" }, filter), toggle, h("span", { class: "muted small" }, `${all.length} calculations in the selected revisions`)),
    holder, detail);
  draw();
  const selected = all.find((c) => c.key === selectedKey);
  if (selected) {
    clear(detail, h("div", { style: "margin-top:12px" },
      h("div", { class: "row" }, h("h2", { style: "margin:0" }, selected.name), h("a", { href: symbolHref(selected.key) }, "open symbol →")),
      calculationCard(selected),
      h("div", { class: "card" }, h("h3", { style: "margin-top:0" }, "Source"), selected.evidence.map((ev) => sourceView(api, ev, { open: true }).el))));
    detail.scrollIntoView({ behavior: "smooth", block: "start" });
  }
}

// ---------------------------------------------------------------- Changes

async function changes(params) {
  const repos = Object.keys(meta.defaultView).sort();
  const repo = params.get("repo") ?? repos.find((r) => revisionsOf(r).length > 1) ?? repos[0];
  const revs = revisionsOf(repo);
  const toSha = params.get("to") ?? selection[repo] ?? revs.at(-1).commitSha;
  const toIndex = Math.max(0, revs.findIndex((r) => r.commitSha === toSha));
  const fromSha = params.get("from") ?? revs[Math.max(0, toIndex - 1)].commitSha;
  const go = (r, f, t) => { location.hash = `#/changes?repo=${encodeURIComponent(r)}&from=${f}&to=${t}`; };

  const picker = h("div", { class: "row card" },
    h("label", {}, "Repository ", h("select", { class: "input", style: "width:auto", onchange: (e) => { const rv = revisionsOf(e.target.value); go(e.target.value, rv[Math.max(0, rv.length - 2)].commitSha, rv.at(-1).commitSha); } }, repos.map((r) => h("option", { value: r, selected: r === repo }, r)))),
    h("label", {}, "from ", h("select", { class: "input", style: "width:auto", onchange: (e) => go(repo, e.target.value, toSha) }, revs.map((r) => h("option", { value: r.commitSha, selected: r.commitSha === fromSha }, r.label)))),
    h("label", {}, "to ", h("select", { class: "input", style: "width:auto", onchange: (e) => go(repo, fromSha, e.target.value) }, revs.map((r) => h("option", { value: r.commitSha, selected: r.commitSha === toSha }, r.label)))));
  clear(main, h("h1", {}, "Changes between revisions"), picker, h("div", { class: "card muted" }, "Loading…"));
  if (fromSha === toSha) {
    main.lastChild.replaceWith(h("div", { class: "card muted" }, revs.length > 1 ? "Pick two different revisions." : "Only one revision of this repository is loaded."));
    return;
  }
  const d = await api(`/api/changes?repo=${encodeURIComponent(repo)}&from=${fromSha}&to=${toSha}`);
  main.lastChild.replaceWith(h("div", {},
    h("p", { class: "muted" }, `${d.from.label} “${d.from.subject ?? ""}” → ${d.to.label} “${d.to.subject ?? ""}”. Continuity is a candidate with reasons; symbols missing from a partial analysis are “not observed”, never removed.`),
    d.addedUnits.length ? h("div", { class: "note" }, `New units: ${d.addedUnits.join(", ")}`) : null,
    d.removedUnits.length ? h("div", { class: "note" }, `Units not present in ${d.to.label}: ${d.removedUnits.join(", ")}`) : null,
    d.units.map(unitChange)));
}

function unitChange(u) {
  const nothing = !u.added.length && !u.removed.length && !u.notObserved.length && !u.continuity.length && !u.calculations.length && !u.edges.length;
  const nodeList = (nodes, tone) => h("div", {}, nodes.map((n) => h("div", { style: "padding:2px 0" }, kindPill(n.kind), " ", h("span", { class: tone }, n.name), h("div", { class: "key" }, n.key))));
  return h("div", { class: "card" },
    h("div", { class: "row" }, h("h2", { style: "margin:0" }, u.unit), h("span", { class: "muted small mono" }, u.unitPath), h("span", { class: "spacer" }),
      u.inputUnchanged ? pill("inputs unchanged", "ok", "Same inputHash: the unit's own files and dependency surfaces did not change") : null,
      statusPill(u.fromStatus), "→", statusPill(u.toStatus)),
    nothing ? h("p", { class: "muted" }, "No changes in this unit.") : null,
    u.toStatus === "partial" ? h("div", { class: "note" }, "The newer analysis is partial: missing symbols are listed as not observed instead of removed.") : null,
    u.continuity.length ? h("div", {}, h("h3", {}, "Continuity candidates (rename / move)"),
      table(["Before", "After", "Reasons"], u.continuity.map((c) => [
        h("div", {}, h("span", { class: "diff-from" }, c.from.name), h("div", { class: "key" }, c.from.key)),
        h("div", {}, h("a", { href: symbolHref(c.to.key), class: "diff-to" }, c.to.name), h("div", { class: "key" }, c.to.key)),
        h("div", {}, c.reasons.map((r) => h("div", { class: "small" }, "• ", r)), c.ambiguous ? pill("ambiguous", "warn") : null)]))) : null,
    u.calculations.length ? h("div", {}, h("h3", {}, "Calculations"), u.calculations.map((c) => h("div", { class: "card", style: "background:var(--panel-2)" },
      h("div", { class: "row" }, h("a", { href: symbolHref(c.toKey), style: "font-weight:650" }, c.name), c.logicChanged ? pill("logic changed", "bad", "Normalised expression differs") : pill("same logic", "ok")),
      c.attributes.length ? table(["Attribute", "Before", "After"], c.attributes.map((a) => [a.name, h("span", { class: "diff-from small" }, a.from ?? "—"), h("span", { class: "diff-to small" }, a.to ?? "—")])) : h("p", { class: "small muted" }, "No attribute changed."),
      c.possibleConsumers.length ? h("div", { class: "small", style: "margin-top:6px" }, h("span", { class: "muted" }, "Possible consumers (static paths, not runtime proof): "), c.possibleConsumers.map((r, i) => [i ? ", " : "", r.declared ? h("a", { href: symbolHref(r.key) }, r.name) : r.name])) : null))) : null,
    u.added.length ? h("div", {}, h("h3", {}, `Added (${u.added.length})`), nodeList(u.added, "diff-to")) : null,
    u.removed.length ? h("div", {}, h("h3", {}, `Removed (${u.removed.length})`), nodeList(u.removed, "diff-from")) : null,
    u.notObserved.length ? h("div", {}, h("h3", {}, `Not observed (${u.notObserved.length})`), nodeList(u.notObserved, "muted")) : null,
    u.edges.length ? h("details", {}, h("summary", { class: "small" }, `${u.edges.length} edge change(s) after continuity mapping`),
      table(["Change", "Relation", "From", "To"], u.edges.map((e) => [pill(e.change, e.change === "added" ? "ok" : e.change === "removed" ? "bad" : "warn"), h("span", { class: "mono small" }, e.relation), h("span", { class: "key" }, e.from), h("span", { class: "key" }, e.to ?? "—")]))) : null);
}

// ---------------------------------------------------------------- Artifacts

async function artifacts() {
  const list = await api("/api/artifacts");
  clear(main,
    h("h1", {}, "Artifacts"),
    h("p", { class: "muted" }, `${list.length} validated artifacts. One artifact is one analysed unit at one exact revision.`),
    meta.problems.length ? h("div", { class: "card error" }, h("h3", { style: "margin-top:0" }, "Rejected files"), meta.problems.map((p) => h("div", { class: "small mono" }, p))) : null,
    table(["Repository", "Revision", "Unit", "Language", "Extractor", "Status", "Nodes / edges / calcs", "Files", "Diagnostics"], list.map((a) => [
      a.repositoryId,
      h("span", {}, a.revision, h("div", { class: "key" }, shortSha(a.commitSha))),
      h("span", {}, a.unit, h("div", { class: "key" }, a.unitPath)),
      a.language,
      h("span", { class: "small" }, a.extractor),
      statusPill(a.status),
      h("span", { class: "mono small" }, `${a.counts.node} / ${a.counts.edge} / ${a.counts.calculation}`),
      h("span", { class: "small" }, `${a.coverage.filesAnalyzed}/${a.coverage.filesTotal}`, a.coverage.incompletePaths?.length ? h("div", { class: "key" }, a.coverage.incompletePaths.join(", ")) : null),
      a.diagnostics.length ? a.diagnostics.map((d) => h("div", { class: "small" }, pill(d.severity, d.severity === "error" ? "bad" : "warn"), " ", h("span", { class: "mono" }, d.code), h("div", { class: "muted" }, d.message))) : h("span", { class: "muted" }, "—"),
    ])));
}

// ---------------------------------------------------------------- Evaluation

let findingFilter = "";

async function evaluation() {
  if (!meta.hasEvaluation) {
    clear(main, h("div", { class: "card" }, h("h1", {}, "No evaluation report loaded"),
      h("p", { class: "muted" }, "Score extractor output and open the explorer on it:"),
      h("pre", { class: "formula" }, "make evaluate ACTUAL=out/\nmake explore ARTIFACTS=out/ EVALUATION=out/evaluation.json")));
    return;
  }
  const r = await api("/api/evaluation");
  const t = r.totals;
  const kinds = [...new Set(r.artifacts.flatMap((a) => a.findings.map((f) => f.kind)))].sort();
  const findings = h("div", {});
  const drawFindings = () => {
    const rows = r.artifacts.flatMap((a) => a.findings.map((f) => ({ a, f }))).filter(({ f }) => !findingFilter || f.kind === findingFilter);
    clear(findings, table(["Artifact", "Kind", "Subject", "Detail"], rows.map(({ a, f }) => [h("span", { class: "small" }, a.label), pill(f.kind, f.kind.startsWith("Missing") ? "bad" : "warn"), h("span", { class: "key" }, f.subject), h("span", { class: "small" }, f.detail)])));
  };
  const filters = h("div", { class: "tabs-inline" }, ["", ...kinds].map((k) => h("button", { class: findingFilter === k ? "active" : undefined, onclick: () => { findingFilter = k; evaluation(); } }, k || "All findings")));

  clear(main,
    h("div", { class: "row" }, h("h1", {}, "Evaluation against golden artifacts"), r.passed ? pill("thresholds passed", "ok") : pill("thresholds failed", "bad")),
    r.thresholdFailures?.length ? h("div", { class: "card error" }, r.thresholdFailures.map((f) => h("div", { class: "small" }, "• ", f))) : null,
    h("div", { class: "card" },
      ratioBar("Node recall", t.nodeRecall), ratioBar("Edge recall", t.edgeRecall), ratioBar("Edge accuracy", t.edgeAccuracy),
      ratioBar("Evidence located", t.evidenceLocated), ratioBar("Calculation recall", t.calculationRecall), ratioBar("Attribute accuracy", t.attributeAccuracy),
      ratioBar("IR hash match", t.irHashMatch), ratioBar("Precision (exhaustive)", t.precision), ratioBar("Coverage", t.coverage)),
    h("h2", {}, "Artifacts"),
    table(["Golden artifact", "Status", "Extractor", "Nodes", "Edges", "Attributes", "Findings"], r.artifacts.map((a) => [
      a.label, pill(a.status, a.status === "Evaluated" ? (a.findings.length ? "warn" : "ok") : "bad"), a.actualExtractor ?? "—",
      `${a.metrics.nodeRecall.hit}/${a.metrics.nodeRecall.total}`, `${a.metrics.edgeRecall.hit}/${a.metrics.edgeRecall.total}`, `${a.metrics.attributeAccuracy.hit}/${a.metrics.attributeAccuracy.total}`,
      a.findings.length || (a.problems.length ? a.problems[0] : "—")])),
    h("h2", {}, "Findings"), filters, findings);
  drawFindings();
}

// ---------------------------------------------------------------- boot

meta = await api("/api/meta");
loadSelection();
window.addEventListener("hashchange", route);
route();
