/** Parse authority separately so canonical URL hosts never change the path slice boundary. */
export function absoluteHttp(value: string): { origin: string; host: string; scheme: string; pathname: string; query: string[] } | undefined {
  const match = /^(https?):\/\/([^/?#]*)([^?#]*)(?:\?([^#]*))?(?:#.*)?$/i.exec(value);
  if (!match || !match[2] || /[{}\s\\|]/.test(match[2]) || /[\\\r\n]/.test(value)) return undefined;
  try {
    const url = new URL(`${match[1]}://${match[2]}/`);
    if (!url.hostname) return undefined;
    return { origin: url.origin, host: url.host, scheme: url.protocol.slice(0, -1), pathname: match[3] || "/", query: queryNames(match[4] ?? "") };
  } catch { return undefined; }
}

export function queryNames(value: string): string[] {
  return [...new Set(value.split("&").map((s) => s.split("=")[0]!).filter((s) => !!s && !s.includes("{}")))].sort();
}

/** Values and fragments never participate in target identity. */
export function splitHttpPath(value: string): { pathname: string; query: string[] } {
  const beforeFragment = value.split("#")[0]!;
  const index = beforeFragment.indexOf("?");
  return index < 0 ? { pathname: beforeFragment, query: [] } : { pathname: beforeFragment.slice(0, index), query: queryNames(beforeFragment.slice(index + 1)) };
}

export const hasAuthorityOrScheme = (value: string) => /^\/\/|^[A-Za-z][A-Za-z\d+.-]*:/.test(value);

/** WHATWG URL removes dot segments but preserves encoded reserved separators such as %2F. */
export function canonicalHttpPath(value: string): string | undefined {
  if (/[|\\\r\n]/.test(value)) return undefined;
  let prefix = "cepathparameter";
  while (value.includes(prefix)) prefix += "x";
  const placeholders: string[] = [];
  const safe = value.replace(/\{[^}]*\}/g, (text) => {
    placeholders.push(text);
    return prefix + (placeholders.length - 1) + "end";
  });
  try {
    let path = new URL(safe, "http://ce.invalid/").pathname;
    for (const [i, placeholder] of placeholders.entries()) path = path.replaceAll(prefix + i + "end", placeholder);
    return path;
  } catch { return undefined; }
}
