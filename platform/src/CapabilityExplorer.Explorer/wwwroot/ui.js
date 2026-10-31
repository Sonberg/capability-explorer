// DOM helpers. All text goes through textContent: artifact content comes from analysed repositories and is
// treated as data, never as markup.

export function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs ?? {})) {
    if (v === undefined || v === null || v === false) continue;
    if (k === "class") el.className = v;
    else if (k.startsWith("on") && typeof v === "function") el.addEventListener(k.slice(2), v);
    else if (k === "dataset") Object.assign(el.dataset, v);
    else el.setAttribute(k, v === true ? "" : String(v));
  }
  append(el, children);
  return el;
}

function append(el, children) {
  for (const c of children.flat(Infinity)) {
    if (c === undefined || c === null || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
}

export function clear(el, ...children) {
  el.replaceChildren();
  append(el, children);
  return el;
}

export const pill = (text, tone = "", title) => h("span", { class: `pill ${tone}`, title }, text);

export function resolutionPill(resolution, reason) {
  const tone = { resolved: "ok", "static-target": "info", candidate: "warn", unresolved: "bad" }[resolution] ?? "";
  const titles = {
    resolved: "Target proven",
    "static-target": "Static target proven; the runtime target is not (interface/virtual dispatch, DI)",
    candidate: "One of several possible targets",
    unresolved: "Target unknown",
  };
  return pill(reason ? `${resolution}: ${reason}` : resolution, tone, titles[resolution]);
}

export function statusPill(status) {
  return pill(status, status === "full" ? "ok" : status === "partial" ? "warn" : "");
}

export const kindPill = (kind) => (kind ? h("span", { class: "pill kind" }, kind) : null);

export function shortSha(sha) {
  return sha ? sha.slice(0, 12) : "";
}

export function symbolHref(key) {
  return `#/explore?key=${encodeURIComponent(key)}`;
}

/** A link to a symbol when it is declared in the view, otherwise its key with a badge saying why it is not. */
export function symbolLink(ref) {
  if (!ref) return h("span", { class: "muted" }, "—");
  if (ref.declared) {
    return h("span", {}, h("a", { href: symbolHref(ref.key), title: ref.key }, ref.name), " ", kindPill(ref.kind), ref.repositoryId ? h("span", { class: "muted small" }, ` ${ref.repositoryId}`) : null);
  }
  const unbound = ref.key.includes("@unbound:");
  const external = ref.key.includes("|@ext|") || ref.key.includes("@builtin:");
  return h("span", { title: ref.key },
    h("span", { class: "mono" }, ref.name), " ",
    pill(unbound ? "unbound" : external ? "external" : "not in view", unbound ? "warn" : ""),
    h("div", { class: "key" }, ref.key));
}

export function table(columns, rows, { onRowClick, rowClass } = {}) {
  return h("div", { class: "table-wrap" }, h("table", {},
    h("thead", {}, h("tr", {}, columns.map((c) => h("th", {}, c)))),
    h("tbody", {}, rows.length === 0
      ? h("tr", {}, h("td", { colspan: columns.length, class: "muted" }, "Nothing here."))
      : rows.map((cells, i) => h("tr", { class: [onRowClick ? "clickable" : "", rowClass?.(i) ?? ""].join(" ").trim() || undefined, onclick: onRowClick ? () => onRowClick(i) : undefined },
        cells.map((c) => h("td", {}, c)))))));
}

export function kv(pairs) {
  return h("dl", { class: "kv" }, pairs.filter(([, v]) => v !== undefined && v !== null && v !== "").flatMap(([k, v]) => [h("dt", {}, k), h("dd", {}, v)]));
}

export function propsList(props) {
  if (!props) return null;
  return kv(Object.entries(props).map(([k, v]) => [k, Array.isArray(v) ? v.join(", ") : typeof v === "string" && v.includes("|") ? h("span", { class: "mono small" }, v) : String(v)]));
}

/** Source snippet viewer: fetches lazily, highlights the cited span. */
export function sourceView(api, ev, { open = false } = {}) {
  const box = h("div", { class: "source" });
  const head = h("div", { class: "path" }, `${ev.path}:${ev.startLine}${ev.endLine !== ev.startLine ? `-${ev.endLine}` : ""}`, h("span", { class: "spacer" }), h("span", {}, shortSha(ev.commitSha)));
  const body = h("pre", {}, h("span", { class: "ln muted" }, "loading…"));
  box.append(head, body);
  const load = async () => {
    try {
      const src = await api(`/api/source?repo=${encodeURIComponent(ev.repositoryId)}&commit=${ev.commitSha}&path=${encodeURIComponent(ev.path)}&start=${ev.startLine}&end=${ev.endLine}`);
      clear(body, src.lines.map((l) => h("span", { class: `ln${l.n >= src.highlightStart && l.n <= src.highlightEnd ? " hl" : ""}`, dataset: { n: l.n } }, l.text || " ")));
    } catch (e) {
      clear(body, h("span", { class: "ln muted" }, e.message));
    }
  };
  if (open) load();
  return { el: box, load };
}

/** Evidence references that expand into source views on click. */
export function evidenceLinks(api, evidence) {
  const wrap = h("div", {});
  const holder = h("div", {});
  const links = h("div", { class: "row" }, (evidence ?? []).map((ev) => h("button", {
    class: "ev-link",
    title: "Show source at this revision",
    onclick: () => {
      const existing = holder.querySelector(`[data-ev="${CSS.escape(ev.id)}"]`);
      if (existing) { existing.remove(); return; }
      const v = sourceView(api, ev, { open: true });
      v.el.dataset.ev = ev.id;
      holder.append(v.el);
    },
  }, `${ev.path.split("/").at(-1)}:${ev.startLine}`)));
  wrap.append(links, holder);
  return wrap;
}

export function ratioBar(label, ratio) {
  const pct = ratio.total === 0 ? 100 : (100 * ratio.hit) / ratio.total;
  return h("div", { class: "metric" },
    h("span", {}, label),
    h("div", { class: `bar${pct < 100 ? " low" : ""}` }, h("div", { style: `width:${pct.toFixed(1)}%` })),
    h("span", { class: "mono small" }, `${ratio.hit}/${ratio.total} · ${pct.toFixed(1)}%`));
}
