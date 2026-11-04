import assert from "node:assert/strict";
import { createHash } from "node:crypto";
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
const pinnedOasSchemaSha256 = "6d1eb9fe8993d218ac034908ebf9a24ba1e4c3be62bd4d7c7a0d1a56778f5a61";

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
  const supportFiles = [
    "oas-3.2-meta-2026-02-26.json",
    "oas-3.2-dialect-2026-02-26.json",
  ];
  const ajv = createAjv();
  for (const file of supportFiles) ajv.addSchema(await json(resolve(vendorRoot, file)));

  const oasSchemaPath = resolve(vendorRoot, "oas-3.2-schema-2026-08-30.json");
  const oasSchemaBytes = await readFile(oasSchemaPath);
  assert.equal(
    createHash("sha256").update(oasSchemaBytes).digest("hex"),
    pinnedOasSchemaSha256,
    "the guarded AJV equivalence rewrite is allowed only for the reviewed pinned source",
  );
  const oasSchema = JSON.parse(oasSchemaBytes.toString("utf8")) as JsonObject;
  assertAjvRewriteInvariants(oasSchema);
  const rewrittenSchema = rewriteDynamicReferences(oasSchema);
  ajv.addSchema(rewrittenSchema);
  ajv.addSchema(await json(resolve(vendorRoot, "oas-3.2-schema-base-2026-08-30.json")));
  const validate = ajv.getSchema(String(oasSchema.$id));
  assert.ok(validate, "pinned official OpenAPI 3.2 structural schema must be compiled from local schemas");
  return { ajv, validate };
}

test("validates design documents with the guarded pinned OpenAPI 3.2 structural schema", async () => {
  const { validate } = await compileOasValidator();
  for (const file of ["central-intake.openapi.json", "process-decisions.openapi.json"]) {
    const document = await json(resolve(openApiRoot, file));
    assert.equal(document.openapi, "3.2.1");
    assert.equal(validate(document), true, JSON.stringify(validate.errors?.slice(0, 5)));
    assert.deepEqual(validate.errors, null);
    assertDesignOnlyBoundary(document);
  }
});

test("rejects missing parameter name and in with the structural schema", async () => {
  const { validate } = await compileOasValidator();
  const original = await json(resolve(openApiRoot, "central-intake.openapi.json"));
  const path = "/v1/collection-submissions/{attemptId}";

  const missingName = structuredClone(original);
  const nameParameters = (((missingName.paths as JsonObject)[path] as JsonObject).get as JsonObject).parameters as JsonObject[];
  delete nameParameters[0]!.name;
  assert.equal(validate(missingName), false);

  const missingLocation = structuredClone(original);
  const locationParameters = (((missingLocation.paths as JsonObject)[path] as JsonObject).get as JsonObject).parameters as JsonObject[];
  delete locationParameters[0]!.in;
  assert.equal(validate(missingLocation), false);
});

test("rejects an OAS version change instead of accepting a 3.1 fallback", async () => {
  const { validate } = await compileOasValidator();
  const document = await json(resolve(openApiRoot, "central-intake.openapi.json"));
  const modified = { ...document, openapi: "3.1.0" };
  assert.equal(validate(modified), false);
});

test("design boundary rejects otherwise-valid servers or security declarations", async () => {
  const { validate } = await compileOasValidator();
  const document = await json(resolve(openApiRoot, "central-intake.openapi.json"));
  const withServer = { ...document, servers: [{ url: "https://example.invalid" }] };
  assert.equal(validate(withServer), true, JSON.stringify(validate.errors?.slice(0, 5)));
  assert.throws(() => assertDesignOnlyBoundary(withServer), /servers are prohibited/);

  const withSecurity = { ...document, security: [] };
  assert.equal(validate(withSecurity), true, JSON.stringify(validate.errors?.slice(0, 5)));
  assert.throws(() => assertDesignOnlyBoundary(withSecurity), /security declarations are prohibited/);
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
  ajv.addSchema(await json(resolve(repositoryRoot, "schema/models/v1.schema.json")));
  ajv.addSchema(await json(resolve(repositoryRoot, "schema/candidates/artifact.schema.json")));
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
  const invalidSubmission = structuredClone(submission) as JsonObject;
  invalidSubmission.unexpected = true;
  assert.equal(submissionValidator(invalidSubmission), false);

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
    const invalidWindow = structuredClone(value) as {
      items: Array<{ freshness: { validUntil: string | null } }>;
    };
    invalidWindow.items[0]!.freshness.validUntil = null;
    assert.equal(validator(invalidWindow), false, "fresh cannot omit its policy deadline");
  }
});

test("rejects a broken local reference", async () => {
  const path = resolve(openApiRoot, "central-intake.openapi.json");
  const document = await json(path);
  const responseSchema = (((document.paths as JsonObject)["/v1/collection-submissions"] as JsonObject).post as JsonObject)
    .responses as JsonObject;
  const successSchema = ((((responseSchema["202"] as JsonObject).content as JsonObject)["application/json"] as JsonObject)
    .schema as JsonObject);
  successSchema.$ref = "#/components/schemas/MissingReceipt";
  await assert.rejects(collectReferences(document, path, new Set()), /unresolved local reference/);
});

function assertAjvRewriteInvariants(schema: JsonObject): void {
  const entries = walk(schema);
  const anchors = entries.filter(({ value }) => isObject(value) && value.$dynamicAnchor === "meta");
  assert.deepEqual(anchors.map(({ path }) => path), ["$defs.schema"]);
  const dynamicRefs = entries.flatMap(({ value }) =>
    isObject(value) && typeof value.$dynamicRef === "string" ? [value.$dynamicRef] : []);
  assert.equal(dynamicRefs.length, 5);
  assert.ok(dynamicRefs.every((reference) => reference === "#meta"));

  const refs = entries.flatMap(({ value }) =>
    isObject(value) && typeof value.$ref === "string" ? [value.$ref] : []);
  assert.ok(refs.length > 0);
  for (const reference of refs) {
    assert.ok(reference.startsWith("#/"), `non-local OAS schema reference is prohibited: ${reference}`);
    assert.notEqual(pointer(schema, reference.slice(1)), undefined, `unresolved OAS schema reference ${reference}`);
  }
}

function rewriteDynamicReferences(value: unknown): JsonObject {
  if (!isObject(value)) throw new Error("pinned OpenAPI structural schema root must be an object");
  const rewritten = rewrite(value) as JsonObject;
  assert.equal(countDynamicReferences(rewritten), 0);
  return rewritten;
}

function rewrite(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(rewrite);
  if (!isObject(value)) return value;
  return Object.fromEntries(Object.entries(value).map(([key, child]) => [
    key === "$dynamicRef" ? "$ref" : key,
    key === "$dynamicRef" ? "#/$defs/schema" : rewrite(child),
  ]));
}

function countDynamicReferences(value: unknown): number {
  return walk(value).filter(({ value: node }) => isObject(node) && "$dynamicRef" in node).length;
}

function walk(value: unknown, path = ""): Array<{ value: unknown; path: string }> {
  const result = [{ value, path }];
  if (Array.isArray(value)) {
    value.forEach((child, index) => result.push(...walk(child, `${path}/${index}`)));
  } else if (isObject(value)) {
    for (const [key, child] of Object.entries(value)) result.push(...walk(child, path ? `${path}.${key}` : key));
  }
  return result;
}

function assertDesignOnlyBoundary(document: JsonObject): void {
  assert.equal(document.openapi, "3.2.1");
  assert.equal(document["x-design-only"], true);
  assert.ok(Array.isArray(document["x-deployment-blockers"]));
  assert.equal(Object.hasOwn(document, "servers"), false, "servers are prohibited by this design-only boundary");
  assert.equal(Object.hasOwn(document, "security"), false, "security declarations are prohibited by this design-only boundary");
}

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
