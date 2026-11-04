import { readFileSync } from "node:fs";
import { Ajv2020, type ValidateFunction } from "ajv/dist/2020.js";
import addFormatsModule from "ajv-formats";

const addFormats = ((addFormatsModule as unknown as { default?: unknown }).default ?? addFormatsModule) as
  (ajv: Ajv2020) => Ajv2020;
const schemas = new Map<string, ValidateFunction>();

function validator(profile: string): ValidateFunction {
  const cached = schemas.get(profile);
  if (cached) return cached;
  const files: Record<string, string> = {
    "ce.models.v1": "../../../../schema/models/v1.schema.json",
    "ce.model-source.v1": "../../../../schema/models/source.v1.schema.json",
    "ce.candidates.v1": "../../../../schema/candidates/artifact.schema.json",
    "ce.collection.v1": "../../../../schema/platform/collection/collection.schema.json",
    "ce.collection.plan.v1": "../../../../schema/platform/collection/plan.schema.json",
  };
  const file = files[profile];
  if (!file) throw new Error("unsupported evidence profile");
  const ajv = new Ajv2020({ allErrors: true, strict: true, strictRequired: false, allowUnionTypes: true });
  addFormats(ajv);
  const schema = JSON.parse(readFileSync(new URL(file, import.meta.url), "utf8")) as Record<string, unknown>;
  const compiled = ajv.compile(schema);
  schemas.set(profile, compiled);
  return compiled;
}

export function validateProfileSchema(profile: string, value: unknown): string[] {
  const validate = validator(profile);
  if (validate(value)) return [];
  return (validate.errors ?? []).slice(0, 8)
    .map(error => `profile schema: ${error.instancePath || "/"} ${error.message}`);
}
