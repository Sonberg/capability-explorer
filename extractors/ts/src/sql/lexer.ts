export type Dialect = "tsql" | "postgresql";

export type TokKind = "word" | "quoted" | "string" | "dollar" | "number" | "var" | "punct";

/** One token. `start`/`end` are absolute offsets into the file text (end exclusive). */
export interface Tok {
  kind: TokKind;
  /** Source text of the token. */
  text: string;
  /** Identifier or literal value: quotes and brackets removed, escapes collapsed. Unchanged for other kinds. */
  value: string;
  start: number;
  end: number;
  /** Dollar-quoted strings only: the offsets of the content between the delimiters. */
  inner?: [number, number];
}

const WORD_START = /[\p{L}_#]/u;
const WORD_PART = /[\p{L}\p{N}_$#@]/u;
const OPERATORS = ["::", "<=", ">=", "<>", "!=", "||", "->>", "->"];

/**
 * Tokenises T-SQL or PostgreSQL text in [from, to). Comments are dropped. Nothing is executed or interpreted:
 * this only separates identifiers, literals and punctuation, keeping absolute positions for evidence.
 */
export function tokenize(text: string, dialect: Dialect, from = 0, to = text.length): Tok[] {
  const out: Tok[] = [];
  let i = from;
  const push = (kind: TokKind, start: number, end: number, value = text.slice(start, end), inner?: [number, number]) =>
    out.push({ kind, text: text.slice(start, end), value, start, end, ...(inner ? { inner } : {}) });

  while (i < to) {
    const c = text[i]!;
    if (c === " " || c === "\t" || c === "\n" || c === "\r" || c === "\f" || c === "﻿") { i++; continue; }
    if (c === "-" && text[i + 1] === "-") {
      while (i < to && text[i] !== "\n") i++;
      continue;
    }
    if (c === "/" && text[i + 1] === "*") {
      // T-SQL block comments nest; so do PostgreSQL's.
      let depth = 1;
      i += 2;
      while (i < to && depth > 0) {
        if (text[i] === "/" && text[i + 1] === "*") { depth++; i += 2; }
        else if (text[i] === "*" && text[i + 1] === "/") { depth--; i += 2; }
        else i++;
      }
      continue;
    }
    if (c === "'") {
      const start = i++;
      let value = "";
      while (i < to) {
        if (text[i] === "'" && text[i + 1] === "'") { value += "'"; i += 2; }
        else if (text[i] === "'") { i++; break; }
        else value += text[i++];
      }
      push("string", start, i, value);
      continue;
    }
    if (c === '"' || (c === "[" && dialect === "tsql")) {
      const close = c === '"' ? '"' : "]";
      const start = i++;
      let value = "";
      while (i < to) {
        if (text[i] === close && text[i + 1] === close) { value += close; i += 2; }
        else if (text[i] === close) { i++; break; }
        else value += text[i++];
      }
      push("quoted", start, i, value);
      continue;
    }
    if (c === "$" && dialect === "postgresql") {
      const m = /^\$([\p{L}_][\p{L}\p{N}_]*)?\$/u.exec(text.slice(i, Math.min(to, i + 64)));
      if (m) {
        const delimiter = m[0];
        const close = text.indexOf(delimiter, i + delimiter.length);
        const end = close < 0 || close + delimiter.length > to ? to : close + delimiter.length;
        const contentEnd = close < 0 || close + delimiter.length > to ? to : close;
        push("dollar", i, end, text.slice(i + delimiter.length, contentEnd), [i + delimiter.length, contentEnd]);
        i = end;
        continue;
      }
      const p = /^\$\d+/.exec(text.slice(i, i + 12));
      if (p) { push("var", i, i + p[0].length); i += p[0].length; continue; }
    }
    if (c === "@" || (c === ":" && /[\p{L}_]/u.test(text[i + 1] ?? "") && text[i - 1] !== ":" && text[i + 1] !== ":" && dialect === "postgresql")) {
      const start = i++;
      while (i < to && WORD_PART.test(text[i]!)) i++;
      if (i === start + 1 && c === "@") { push("punct", start, i); continue; }
      push("var", start, i);
      continue;
    }
    if (/[0-9]/.test(c) || (c === "." && /[0-9]/.test(text[i + 1] ?? ""))) {
      const start = i;
      while (i < to && /[0-9.]/.test(text[i]!)) i++;
      if ((text[i] === "e" || text[i] === "E") && /[0-9+-]/.test(text[i + 1] ?? "")) { i += 2; while (i < to && /[0-9]/.test(text[i]!)) i++; }
      push("number", start, i);
      continue;
    }
    if (WORD_START.test(c)) {
      const start = i++;
      while (i < to && WORD_PART.test(text[i]!)) i++;
      // N'unicode' and E'escape' string prefixes.
      if ((i - start === 1) && /[NnEe]/.test(c) && text[i] === "'") continue;
      push("word", start, i);
      continue;
    }
    const op = OPERATORS.find((o) => text.startsWith(o, i) && i + o.length <= to);
    if (op) { push("punct", i, i + op.length); i += op.length; continue; }
    push("punct", i, i + 1);
    i++;
  }
  return out;
}

export const upper = (t: Tok | undefined) => (t && (t.kind === "word") ? t.value.toUpperCase() : "");
export const isKw = (t: Tok | undefined, ...words: string[]) => t?.kind === "word" && words.includes(t.value.toUpperCase());
export const isP = (t: Tok | undefined, p: string) => t?.kind === "punct" && t.text === p;
export const isIdent = (t: Tok | undefined) => t?.kind === "word" || t?.kind === "quoted";

/** Line index for converting offsets to the schema's 1-based line/column positions. */
export class LineIndex {
  private readonly starts: number[] = [0];
  constructor(text: string) {
    for (let i = 0; i < text.length; i++) if (text[i] === "\n") this.starts.push(i + 1);
  }
  pos(offset: number): { line: number; column: number } {
    let lo = 0;
    let hi = this.starts.length - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (this.starts[mid]! <= offset) lo = mid; else hi = mid - 1;
    }
    return { line: lo + 1, column: offset - this.starts[lo]! + 1 };
  }
  span(start: number, end: number) {
    const s = this.pos(start);
    const e = this.pos(end);
    return { startLine: s.line, startColumn: s.column, endLine: e.line, endColumn: e.column };
  }
}
