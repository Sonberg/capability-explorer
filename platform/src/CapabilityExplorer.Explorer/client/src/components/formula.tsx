import { Fragment, type ReactNode } from "react";
import type { Expr, Json } from "../api";

// Renders the ce.v1 calculation IR as readable pseudo-code. Presentation only: the IR is the source of truth.

export function shortTarget(key?: string): string {
  if (!key) return "?";
  const last = key.split("|").at(-1) ?? key;
  if (key.startsWith("cs|")) {
    const body = last.replace(/^[A-Z]:/, "").replace(/\(.*$/, "");
    return body.split(".").slice(-2).join(".").replace(/``\d+$/, "");
  }
  if (key.startsWith("ts|")) return last.split("#")[0];
  return last;
}

const Op = ({ children }: { children: ReactNode }) => <span className="font-semibold text-blue-700 dark:text-blue-300">{children}</span>;
const Fn = ({ children, title }: { children: ReactNode; title?: string }) => (
  <span title={title} className="text-amber-700 dark:text-amber-300">
    {children}
  </span>
);
const Lit = ({ children }: { children: ReactNode }) => <span className="text-emerald-700 dark:text-emerald-300">{children}</span>;

const isExpr = (v: Json | undefined): v is Expr => !!v && typeof v === "object" && !Array.isArray(v) && typeof (v as Record<string, Json>).op === "string";
const list = (v: Json | undefined): Expr[] => (Array.isArray(v) ? v.filter(isExpr) : []);

function join(items: ReactNode[], sep: ReactNode): ReactNode[] {
  return items.flatMap((n, i) => (i === 0 ? [<Fragment key={i}>{n}</Fragment>] : [<Fragment key={`s${i}`}>{sep}</Fragment>, <Fragment key={i}>{n}</Fragment>]));
}

function R({ e }: { e: Json | undefined }): ReactNode {
  if (!isExpr(e)) return <span className="text-red-600">?</span>;
  const s = (k: string) => (typeof e[k] === "string" ? (e[k] as string) : String(e[k] ?? ""));
  switch (e.op) {
    case "lit": {
      const v = e.value;
      return <Lit>{v === null ? "null" : typeof v === "string" && !/^[\d.]+$/.test(v) && v !== "NaN" ? JSON.stringify(v) : String(v)}</Lit>;
    }
    case "var":
      return <>{s("ref")}</>;
    case "member":
      return <><R e={e.of} />.{s("name")}</>;
    case "index":
      return <><R e={e.of} />[<R e={e.key} />]</>;
    case "bin":
      return <>(<R e={e.l} /> <Op>{s("o")}</Op> <R e={e.r} />)</>;
    case "cmp":
      return <><R e={e.l} /> <Op>{s("o")}</Op> <R e={e.r} /></>;
    case "logic": {
      const args = list(e.args);
      return s("o") === "not" ? <><Op>!</Op><R e={args[0]} /></> : <>{join(args.map((a, i) => <R key={i} e={a} />), <Op> {s("o") === "and" ? "&&" : "||"} </Op>)}</>;
    }
    case "cond":
      return <>if <R e={e.test} />{"\n  then "}<R e={e.then} />{"\n  else "}<R e={e.else} /></>;
    case "coalesce":
      return <><Fn>coalesce</Fn>({join(list(e.args).map((a, i) => <R key={i} e={a} />), ", ")})</>;
    case "conv":
      return <><Fn>{s("to")}</Fn>(<R e={e.e} />)</>;
    case "round":
      return <><Fn title={typeof e.target === "string" ? e.target : undefined}>round⟨{s("mode")}⟩</Fn>(<R e={e.e} />{isExpr(e.digits) ? <>, <R e={e.digits} /></> : null})</>;
    case "agg":
      return (
        <>
          <Fn>{s("fn")}</Fn>({e.bind ? `${s("bind")} in ` : ""}<R e={e.over} />
          {isExpr(e.filter) && <> <Op>where</Op> <R e={e.filter} /></>}
          {isExpr(e.value) && <> <Op>→</Op> <R e={e.value} /></>})
        </>
      );
    case "fold":
      return (
        <>
          <Fn>fold</Fn>({s("bind")} in <R e={e.over} />{e.skip ? ` skip ${s("skip")}` : ""}; {s("acc")} = <R e={e.init} />; {s("acc")} ← <R e={e.step} />)
        </>
      );
    case "call":
      return <><Fn title={s("target")}>{shortTarget(s("target"))}</Fn>({join(list(e.args).map((a, i) => <R key={i} e={a} />), ", ")})</>;
    case "tuple":
      return <>({join(list(e.items).map((a, i) => <R key={i} e={a} />), ", ")})</>;
    case "opaque":
      return <span className="text-zinc-500">⟨{s("reason")}⟩</span>;
    default:
      return <span className="text-red-600">⟨unknown op {e.op}⟩</span>;
  }
}

export function Formula({ expr, inputs }: { expr: Expr; inputs?: { ref: string; type: string; origin?: string }[] }) {
  return (
    <div>
      <div className="whitespace-pre-wrap break-words rounded-md bg-zinc-100 px-3 py-2.5 font-mono text-[12.5px] leading-relaxed dark:bg-zinc-800/70">
        <R e={expr} />
      </div>
      {inputs && inputs.length > 0 && (
        <div className="mt-1.5 space-y-0.5 text-xs text-zinc-500">
          {inputs.map((i) => (
            <div key={i.ref}>
              <span className="font-mono">{i.ref}</span> : {i.type}
              {i.origin ? ` (from ${shortTarget(i.origin)})` : ""}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
