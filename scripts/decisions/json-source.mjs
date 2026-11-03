// Strict JSON, including duplicate-key rejection (JSON.parse alone loses that evidence).
export function strictJSON(text) {
  let at = 0;
  const ws = () => { while (/[\u0009\u000a\u000d\u0020]/u.test(text[at] ?? '') && at < text.length) at++; };
  function value(depth) {
    if (depth > 100) throw new Error('JSON depth budget');
    ws();
    const start = at, first = text[at++];
    if (first === '{' || first === '[') {
      const object = first === '{', pairs = [], array = [], keys = new Set(), end = object ? '}' : ']';
      ws();
      if (text[at] === end) { at++; return object ? {} : []; }
      while (at < text.length) {
        if (object) {
          ws();
          if (text[at] !== '"') throw new Error('JSON object key');
          const k = value(depth + 1);
          if (keys.has(k)) throw new Error(`duplicate JSON key: ${k}`);
          keys.add(k); ws();
          if (text[at++] !== ':') throw new Error('JSON colon');
          pairs.push([k, value(depth + 1)]);
        } else array.push(value(depth + 1));
        ws();
        if (text[at] === end) { at++; return object ? Object.fromEntries(pairs) : array; }
        if (text[at++] !== ',') throw new Error('JSON comma');
      }
      throw new Error('unclosed JSON container');
    }
    if (first === '"') {
      while (at < text.length) {
        const c = text[at++];
        if (c === '\\') at++;
        else if (c === '"') return JSON.parse(text.slice(start, at));
      }
      throw new Error('unclosed JSON string');
    }
    at = start;
    const m = /^(?:true|false|null|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?)/u.exec(text.slice(at));
    if (!m) throw new Error('JSON value');
    at += m[0].length;
    const parsed = JSON.parse(m[0]);
    if (typeof parsed === 'number' && !Number.isFinite(parsed)) throw new Error('JSON number out of range');
    return parsed;
  }
  const result = value(0);
  ws();
  if (at !== text.length) throw new Error('trailing JSON data');
  return result;
}

export const escapePointer = s => s.replaceAll('~', '~0').replaceAll('/', '~1');
export function resolvePointer(value, pointer) {
  if (pointer === '') return value;
  if (!pointer.startsWith('/') || /~(?![01])/u.test(pointer)) throw new Error('invalid JSON pointer');
  for (const token of pointer.slice(1).split('/')) {
    const k = token.replaceAll('~1', '/').replaceAll('~0', '~');
    if (value === null || typeof value !== 'object' || !Object.hasOwn(value, k)) throw new Error(`unresolved JSON pointer: ${pointer}`);
    value = value[k];
  }
  return value;
}

export function parseCollection(bytes, profile) {
  const text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes), lines = text.split('\n');
  const document = strictJSON(text), collection = resolvePointer(document, profile.collection);
  if (!Array.isArray(collection)) throw new Error('JSON collection must be an array');
  const consumed = new Set([profile.id, profile.title, profile.status, ...profile.relations.map(r => r.field)]);
  return collection.map((entry, index) => {
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) throw new Error('JSON decision must be an object');
    for (const field of [profile.id, profile.title, profile.status])
      if (typeof entry[field] !== 'string' || !entry[field].trim()) throw new Error(`JSON scalar field: ${field}`);
    const pointer = `${profile.collection}/${index}`;
    const declaredRelations = [];
    for (const relation of profile.relations) {
      if (!Object.hasOwn(entry, relation.field)) continue;
      const values = entry[relation.field];
      if (!Array.isArray(values) || !values.every(v => typeof v === 'string' && v.trim()) ||
          new Set(values).size !== values.length) throw new Error(`unsupported JSON relation field: ${relation.field}`);
      values.forEach((target, i) => declaredRelations.push({
        type: relation.type, direction: relation.direction, target,
        pointer: `${pointer}/${escapePointer(relation.field)}/${i}`
      }));
    }
    return {
      text, lines, document, pointer, alias: entry[profile.id], title: entry[profile.title],
      titleLine: 1, titleEnd: lines.length, rawStatus: entry[profile.status],
      statusLine: 1, statusEnd: lines.length, statusPointer: `${pointer}/${escapePointer(profile.status)}`,
      titlePointer: `${pointer}/${escapePointer(profile.title)}`,
      declaredRelations,
      metadata: Object.entries(entry).map(([key, value]) => ({ key, value, pointer: `${pointer}/${escapePointer(key)}` })),
      diagnostics: Object.keys(entry).filter(k => !consumed.has(k)).map(k => `uninterpreted JSON metadata: ${k}`)
    };
  });
}
