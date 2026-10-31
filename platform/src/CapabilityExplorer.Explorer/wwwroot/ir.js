// Renders the ce.v1 calculation IR as readable pseudo-code. Pure presentation: the IR is the source of truth.
import { h } from "./ui.js";

const op = (t) => h("span", { class: "op" }, t);
const fn = (t) => h("span", { class: "fn" }, t);
const lit = (t) => h("span", { class: "lit" }, t);

export function shortTarget(key) {
  if (!key) return "?";
  const last = key.split("|").at(-1);
  if (key.startsWith("cs|")) {
    const body = last.replace(/^[A-Z]:/, "").replace(/\(.*$/, "");
    const parts = body.split(".");
    return parts.slice(-2).join(".").replace(/``\d+$/, "");
  }
  if (key.startsWith("ts|")) return last.split("#")[0];
  return last;
}

function join(nodes, sep) {
  return nodes.flatMap((n, i) => (i === 0 ? [n] : [sep, n]));
}

export function render(e, names = {}) {
  const r = (x) => render(x, names);
  switch (e.op) {
    case "lit":
      return lit(e.value === null ? "null" : typeof e.value === "string" && !/^[\d.]+$/.test(e.value) && e.value !== "NaN" ? JSON.stringify(e.value) : String(e.value));
    case "var":
      return h("span", { title: names[e.ref] ?? "" }, names[e.ref] ? `${names[e.ref]}` : e.ref);
    case "member":
      return [r(e.of), ".", e.name];
    case "index":
      return [r(e.of), "[", r(e.key), "]"];
    case "bin":
      return ["(", r(e.l), " ", op(e.o), " ", r(e.r), ")"];
    case "cmp":
      return [r(e.l), " ", op(e.o), " ", r(e.r)];
    case "logic":
      return e.o === "not" ? [op("!"), r(e.args[0])] : join(e.args.map(r), [" ", op(e.o === "and" ? "&&" : "||"), " "]);
    case "cond":
      return ["if ", r(e.test), "\n  then ", r(e.then), "\n  else ", r(e.else)];
    case "coalesce":
      return [fn("coalesce"), "(", join(e.args.map(r), ", "), ")"];
    case "conv":
      return [fn(e.to), "(", r(e.e), ")"];
    case "round":
      return [fn(`round⟨${e.mode}⟩`), "(", r(e.e), e.digits ? [", ", r(e.digits)] : "", ")"];
    case "agg":
      return [fn(e.fn), "(", e.bind ? `${e.bind} in ` : "", r(e.over),
        e.filter ? [" ", op("where"), " ", r(e.filter)] : "",
        e.value ? [" ", op("→"), " ", r(e.value)] : "", ")"];
    case "fold":
      return [fn("fold"), "(", `${e.bind} in `, r(e.over), e.skip ? ` skip ${e.skip}` : "", "; ", `${e.acc} = `, r(e.init), "; ", `${e.acc} ← `, r(e.step), ")"];
    case "call":
      return [h("span", { class: "fn", title: e.target }, shortTarget(e.target)), "(", join(e.args.map(r), ", "), ")"];
    case "tuple":
      return ["(", join(e.items.map(r), ", "), ")"];
    case "opaque":
      return h("span", { class: "muted" }, `⟨${e.reason}⟩`);
    default:
      return h("span", { class: "error" }, `⟨unknown op ${e.op}⟩`);
  }
}

export function formula(expr, inputs) {
  const legend = (inputs ?? []).map((i) => h("div", { class: "small muted" }, h("span", { class: "mono" }, i.ref), ` : ${i.type}`, i.origin ? ` (from ${shortTarget(i.origin)})` : ""));
  return h("div", {}, h("div", { class: "formula" }, render(expr)), legend.length ? h("div", { style: "margin-top:6px" }, legend) : null);
}
