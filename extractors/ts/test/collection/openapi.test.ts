import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import { Ajv2020 } from "ajv/dist/2020.js";
import type { ValidateFunction } from "ajv";
import addFormatsModule from "ajv-formats";

type JsonObject = Record<string, unknown>;
const addFormats = ((addFormatsModule as unknown as { default?: unknown }).default ?? addFormatsModule) as
  (ajv: Ajv2020) => Ajv2020;
const testDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(testDirectory, "../../../../");
const openApiRoot = resolve(repositoryRoot, "schema/openapi");
const platformRoot = resolve(repositoryRoot, "schema/platform");
const vendorRoot = resolve(openApiRoot, "vendor");

async function json(path: string): Promise<JsonObject> {
  return JSON.parse(await readFile(path, "utf8")) as JsonObject;
}

function createAjv(): Ajv2020 {
  const ajv = new Ajv2020({
    allErrors: true,
    strict: true,
    strictTypes: false,
    strictRequired: false,
    allowMatchingProperties: true,
  });
  addFormats(ajv);
  ajv.addFormat("media-range", /^(?:\*\/\*|[!#$%&'*+.^_`|~0-9A-Za-z-]+\/(?:\*|[!#$%&'*+.^_`|~0-9A-Za-z-]+))(?:\s*;\s*[!#$%&'*+.^_`|~0-9A-Za-z-]+=(?:"[^"\r\n]*"|[!#$%&'*+.^_`|~0-9A-Za-z-]+))*$/);
  ajv.addFormat("binary", () => true);
  return ajv;
}

async function compileOasValidator(): Promise<{ ajv: Ajv2020; validate: ValidateFunction }> {
  const files = [
    "oas-3.2-meta-2026-02-26.json",
    "oas-3.2-dialect-2026-02-26.json",
    "oas-3.2-schema-2026-08-30.json",
    "oas-3.2-schema-base-2026-08-30.json",
  ];
  const ajv = createAjv();
  for (const file of files) ajv.addSchema(await json(resolve(vendorRoot, file)));
  const oasSchema = await json(resolve(vendorRoot, files[2]!));
  const validate = ajv.getSchema(String(oasSchema.$id));
  assert.ok(validate, "pinned official OpenAPI 3.2 structural schema must be compiled from local schemas");
  return { ajv, validate };
}

test("records the AJV 8.20 incompatibility with the pinned official OpenAPI 3.2 structural schema", async () => {
  const { validate } = await compileOasValidator();
  for (const file of ["central-intake.openapi.json", "process-decisions.openapi.json"]) {
    const document = await json(resolve(openApiRoot, file));
    assert.equal(document.openapi, "3.2.1");
    assert.equal(document["x-design-only"], true);
    assert.ok(Array.isArray(document["x-deployment-blockers"]));
    assert.equal(Object.hasOwn(document, "servers"), false);
    assert.equal(Object.hasOwn(document, "security"), false);
    assert.equal(validate(document), false);
    assert.ok(validate.errors?.length, "the validator must report its unsupported OAS 3.2 schema-object validation");
  }
});

test("rejects an OAS version change instead of accepting a 3.1 fallback", async () => {
  const { validate } = await compileOasValidator();
  const document = await json(resolve(openApiRoot, "central-intake.openapi.json"));
  const modified = { ...document, openapi: "3.1.0" };
  assert.equal(validate(modified), false);
});

test("all API references and examples resolve to local files without a remote loader", async () => {
  const { ajv } = await compileOasValidator();
  const docs = new Map<string, JsonObject>();
  const refs = new Set<string>();
  for (const file of ["central-intake.openapi.json", "process-decisions.openapi.json"]) {
    const path = resolve(openApiRoot, file);
    const document = await json(path);
    docs.set(path, document);
    await collectReferences(document, path, refs);
  }

  assert.ok(refs.size > 0);
  const collectionSchemaPath = resolve(platformRoot, "collection/collection.schema.json");
  const planSchemaPath = resolve(platformRoot, "collection/plan.schema.json");
  const recordSchemaPath = resolve(repositoryRoot, "schema/v1/record.schema.json");
  const collectionSchema = await json(collectionSchemaPath);
  const planSchema = await json(planSchemaPath);
  const recordSchema = await json(recordSchemaPath);
  const platformSchema = await json(resolve(platformRoot, "v1/envelope.schema.json"));
  ajv.addSchema(collectionSchema);
  ajv.addSchema(planSchema);
  ajv.addSchema(recordSchema);
  ajv.addSchema(platformSchema);

  const submission = await json(resolve(platformRoot, "collection/examples/submission.example.json"));
  const manifestValidator = ajv.getSchema(`${String(collectionSchema.$id)}`);
  const attemptValidator = ajv.getSchema(`${String(collectionSchema.$id)}#/$defs/attempt`);
  assert.ok(manifestValidator && attemptValidator);
  assert.equal(manifestValidator(submission.manifest), true);
  assert.equal(attemptValidator(submission.attempt), true);
  assert.ok(Array.isArray(submission.artifacts));
  const manifest = submission.manifest as { content: { inputs: Array<{ artifactPath: string; status: string }> } };
  assert.equal(submission.artifacts.length, manifest.content.inputs.filter((input) => input.status === "included").length);

  const central = docs.get(resolve(openApiRoot, "central-intake.openapi.json"))!;
  const centralSchemas = ((central.components as JsonObject).schemas as JsonObject);
  const centralSchemaCopy = rewriteSelfReferences(structuredClone(centralSchemas));
  const submissionValidator = ajv.compile({
    $id: "https://capability-explorer/schema/openapi/validation-submission.json",
    $defs: { components: { schemas: centralSchemaCopy } },
    $ref: "#/$defs/components/schemas/Submission",
  });
  assert.equal(submissionValidator(submission), true, JSON.stringify(submissionValidator.errors?.slice(0, 5)));

  const process = docs.get(resolve(openApiRoot, "process-decisions.openapi.json"))!;
  const schemas = (process.components as JsonObject).schemas as JsonObject;
  for (const [name, examplePath] of [
    ["ProcessPage", "process-page.example.json"],
    ["DecisionPage", "decision-page.example.json"],
  ] as const) {
    const componentSchemas = rewriteSelfReferences(structuredClone(schemas));
    const wrapper = {
      $id: `https://capability-explorer/schema/openapi/validation-${name}.json`,
      $defs: { components: { schemas: componentSchemas } },
      $ref: `#/$defs/components/schemas/${name}`,
    };
    const validator = ajv.compile(wrapper);
    const value = await json(resolve(platformRoot, `collection/examples/${examplePath}`));
    assert.equal(validator(value), true, JSON.stringify(validator.errors?.slice(0, 5)));
  }
});

function rewriteSelfReferences(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(rewriteSelfReferences);
  if (!isObject(value)) return value;
  return Object.fromEntries(Object.entries(value).map(([key, child]) => [
    key,
    key === "$ref" && typeof child === "string"
      ? child.replace(/^#\/components\/schemas\//, "#/$defs/components/schemas/")
      : rewriteSelfReferences(child),
  ]));
}

async function collectReferences(value: unknown, fromPath: string, refs: Set<string>): Promise<void> {
  if (Array.isArray(value)) {
    for (const item of value) await collectReferences(item, fromPath, refs);
    return;
  }
  if (!isObject(value)) return;
  for (const [key, child] of Object.entries(value)) {
    if ((key === "$ref" || key === "externalValue") && typeof child === "string") {
      assert.equal(/^[a-z][a-z0-9+.-]*:/i.test(child), false, `remote reference is prohibited: ${child}`);
      const [referencePath, fragment] = child.split("#", 2);
      const targetPath = referencePath ? resolve(dirname(fromPath), referencePath) : fromPath;
      const target = await json(targetPath);
      if (fragment) assert.ok(pointer(target, fragment), `unresolved local reference ${child}`);
      refs.add(`${targetPath}#${fragment ?? ""}`);
    } else {
      await collectReferences(child, fromPath, refs);
    }
  }
}

function pointer(value: unknown, fragment: string): unknown {
  if (!fragment.startsWith("/")) return value;
  let current = value;
  for (const part of fragment.slice(1).split("/")) {
    const key = part.replace(/~1/g, "/").replace(/~0/g, "~");
    if (Array.isArray(current)) current = current[Number(key)];
    else if (isObject(current)) current = current[key];
    else return undefined;
    if (current === undefined) return undefined;
  }
  return current;
}

function isObject(value: unknown): value is JsonObject {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
