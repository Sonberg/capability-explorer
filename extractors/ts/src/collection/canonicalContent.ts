export const canonicalizationRecipe = "ce.collection.v1/utf16-escaped-json";

export function canonicalContentBytes(content: object): string {
  const body = encode(content, 0);
  if (body.length > 6 * 1_048_576) throw new Error("canonical collection metadata exceeds its bound");
  return `${canonicalizationRecipe}\0${body}`;
}

function encode(value: unknown, depth: number): string {
  if (depth > 32) throw new Error("canonical collection exceeds its depth bound");
  if (value === null) return "null";
  if (typeof value === "boolean") return String(value);
  if (typeof value === "number") {
    if (!Number.isSafeInteger(value)) throw new Error("canonical collection numbers must be safe integers");
    return String(value);
  }
  if (typeof value === "string") {
    let result = '"';
    for (let index = 0; index < value.length; index++) {
      const code = value.charCodeAt(index);
      if (code >= 0xd800 && code <= 0xdbff &&
          !(value.charCodeAt(index + 1) >= 0xdc00 && value.charCodeAt(index + 1) <= 0xdfff) ||
          code >= 0xdc00 && code <= 0xdfff &&
          !(index > 0 && value.charCodeAt(index - 1) >= 0xd800 && value.charCodeAt(index - 1) <= 0xdbff)) {
        throw new Error("canonical collection strings must be well-formed Unicode");
      }
      result += `\\u${code.toString(16).padStart(4, "0")}`;
    }
    return `${result}"`;
  }
  if (Array.isArray(value)) return `[${value.map(item => encode(item, depth + 1)).join(",")}]`;
  if (typeof value === "object") {
    const record = value as Record<string, unknown>;
    return `{${Object.keys(record).sort().map(key => `${encode(key, depth + 1)}:${encode(record[key], depth + 1)}`).join(",")}}`;
  }
  throw new Error("unsupported canonical collection value");
}
