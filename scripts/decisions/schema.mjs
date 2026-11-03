import { readFileSync } from 'node:fs';

const schema = JSON.parse(readFileSync(new URL('../../schema/decisions/ledger.schema.json', import.meta.url)));
const supported = new Set(['$schema', '$id', '$ref', '$defs', 'type', 'enum', 'const', 'properties',
  'additionalProperties', 'required', 'items', 'minItems', 'uniqueItems', 'minimum', 'maximum', 'pattern', 'minLength']);

export function auditSchema(node = schema) {
  for (const [k, v] of Object.entries(node)) {
    if (!supported.has(k)) throw new Error(`unsupported schema keyword: ${k}`);
    if (k === '$ref' && !schema.$defs[v.slice('#/$defs/'.length)]) throw new Error(`unsupported schema ref: ${v}`);
    if (['properties', '$defs'].includes(k)) Object.values(v).forEach(n => auditSchema(n));
    if (k === 'items') auditSchema(v);
  }
}

// Deliberately only the audited keyword subset above, NOT a general JSON Schema engine.
export function validate(data, definition) {
  function visit(value, rule, path) {
    const fail = why => { throw new Error(`schema ${path}: ${why}`); };
    if (rule.$ref) return visit(value, schema.$defs[rule.$ref.slice('#/$defs/'.length)], path);
    if (rule.const !== undefined && value !== rule.const) fail('const');
    if (rule.enum && !rule.enum.includes(value)) fail('enum');
    const type = Array.isArray(value) ? 'array' : value === null ? 'null' : typeof value;
    if (rule.type && (rule.type === 'integer' ? !Number.isSafeInteger(value) : rule.type !== type)) fail('type');
    if (typeof value === 'string') {
      if (rule.minLength && [...value].length < rule.minLength) fail('minLength');
      if (rule.pattern && !new RegExp(rule.pattern, 'u').test(value)) fail('pattern');
    }
    if (typeof value === 'number') {
      if (rule.minimum !== undefined && value < rule.minimum) fail('minimum');
      if (rule.maximum !== undefined && value > rule.maximum) fail('maximum');
    }
    if (type === 'object') {
      for (const k of rule.required ?? []) if (!Object.hasOwn(value, k)) fail(`required ${k}`);
      for (const [k, v] of Object.entries(value)) {
        if (!Object.hasOwn(rule.properties ?? {}, k)) {
          if (rule.additionalProperties === false) fail(`unknown field ${k}`);
        } else visit(v, rule.properties[k], `${path}.${k}`);
      }
    }
    if (type === 'array') {
      if (rule.minItems && value.length < rule.minItems) fail('minItems');
      if (rule.uniqueItems && new Set(value.map(v => JSON.stringify(v))).size !== value.length) fail('uniqueItems');
      value.forEach((v, i) => visit(v, rule.items, `${path}[${i}]`));
    }
  }
  auditSchema();
  if (!schema.$defs[definition]) throw new Error(`unknown schema definition ${definition}`);
  visit(data, schema.$defs[definition], definition);
}
