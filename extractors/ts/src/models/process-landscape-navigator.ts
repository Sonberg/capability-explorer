import { createHash } from "node:crypto";
import { Ajv2020, type ValidateFunction } from "ajv/dist/2020.js";
import schema from "../../../../schema/models/process-landscape.v1.schema.json" with { type: "json" };
import { parseStrictJson } from "../collection/strictJson.ts";
import type { LandscapeDiagnostic, NavigatorBundleIdentity, NavigatorCategoryName, ProcessLandscapeNavigator } from "./process-landscape-types.ts";
import { ProcessLandscapeError } from "./process-landscape-types.ts";

/**
 * The bundle is a generated static-navigator `.js` asset, not a module: it must never be imported,
 * required, or evaluated (`eval`/`new Function`/`vm`), because that would execute arbitrary
 * generated script content as code. Instead it is treated purely as data: the exact wrapper
 * `window.NAV_DATA = <value>;` is matched textually (optional trailing whitespace only), and the
 * captured value is parsed strictly as JSON (`JSON.parse` via the shared strict-JSON parser), never
 * executed. Anything that does not match this exact wrapper is rejected before parsing.
 */
const WRAPPER_PATTERN = /^window\.NAV_DATA\s*=\s*([\s\S]*?);\s*$/;

/**
 * Maps the bundle's real source-key spellings to the canonical `ProcessLandscapeNavigator` field
 * names. These are generic, structural key names (not private business values), so they are mapped
 * directly: `files` holds the process catalogue and `el` holds the element metadata, while
 * `links`/`events`/`lifecycle`/`journey`/`contexts`/`procFile`/`dmn`/`phases` already use the field
 * name they are preserved under.
 */
const CATEGORY_SOURCE_KEYS: Record<NavigatorCategoryName, string> = {
  catalogue: "files",
  elements: "el",
  links: "links",
  events: "events",
  lifecycle: "lifecycle",
  journey: "journey",
  contexts: "contexts",
  procFile: "procFile",
  dmn: "dmn",
  phases: "phases",
};
const CATEGORY_NAMES = Object.keys(CATEGORY_SOURCE_KEYS) as NavigatorCategoryName[];

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

const categoryValidators = new Map<NavigatorCategoryName, ValidateFunction>();
let categoryAjv: Ajv2020 | undefined;

function validCategory(category: NavigatorCategoryName, value: unknown): boolean {
  let validate = categoryValidators.get(category);
  if (!validate) {
    categoryAjv ??= new Ajv2020({ strict: true, strictRequired: false }).addSchema(schema);
    validate = categoryAjv.compile({ $ref: `${schema.$id}#/$defs/navigator/properties/${category}` });
    categoryValidators.set(category, validate);
  }
  if (!validate(value)) return false;
  // JSON Schema counts code points; the API's 1024-character path bound counts UTF-16 code units.
  for (const reference of fileReferences(category, value)) {
    if (typeof reference === "string" && reference.length > 1024) return false;
  }
  return true;
}

function* fileReferences(category: NavigatorCategoryName, value: unknown): Generator<unknown> {
  const map = value as Record<string, unknown>;
  const rows = value as Array<Record<string, unknown>>;
  switch (category) {
    case "catalogue":
      for (const row of rows) yield row.file;
      break;
    case "elements":
    case "links":
      for (const [file, elements] of Object.entries(map)) {
        yield file;
        if (category === "elements") for (const element of Object.values(elements as Record<string, Record<string, unknown>>)) yield element.tgt;
      }
      break;
    case "events":
      for (const event of Object.values(map) as Array<Record<string, string[][]>>) {
        for (const key of ["throw", "catch", "ref"]) for (const tuple of event[key] ?? []) yield tuple[0];
      }
      break;
    case "lifecycle":
      for (const row of rows) for (const values of Object.values((row.set ?? {}) as Record<string, string[][]>)) {
        for (const tuple of values) yield tuple[0];
      }
      break;
    case "journey":
      for (const phase of (map.phases ?? []) as Array<{ files?: string[] }>) yield* (phase.files ?? []);
      for (const entry of (map.entries ?? []) as Array<{ file?: string }>) yield entry.file;
      break;
    case "procFile": yield* Object.values(map); break;
  }
}

interface NavigatorParseResult {
  navigator: ProcessLandscapeNavigator;
  diagnostics: LandscapeDiagnostic[];
}

/** Admitted BPMN content hashes, in both their raw-byte form and a line-ending-normalized form. */
export interface AdmittedBpmnHashes {
  raw: ReadonlySet<string>;
  /** sha256 of each admitted BPMN file's decoded text with CRLF/CR line endings normalized to LF. */
  newlineNormalized: ReadonlySet<string>;
}

function newlineNormalizedSha256(text: string): string {
  return createHash("sha256").update(text.replace(/\r\n?/g, "\n"), "utf8").digest("hex");
}

/**
 * Parses and validates the bounded navigator-data bundle against the admitted BPMN inputs for this
 * snapshot (identified by their raw-byte sha256, computed from the already-admitted `bpmn` manifest
 * entries). The bundle's own `xml` map duplicates BPMN file contents; each entry is cross-checked by
 * content hash against the admitted BPMN set so the snapshot never silently serves navigator data
 * that was generated from stale or unadmitted BPMN bytes. If any `xml` entry does not match an
 * admitted input, the bundle is recorded as unverified (`verified: false`) and no navigator category
 * data is included -- only the bundle identity and diagnostics are, so callers can see *that* the
 * bundle failed verification without ever receiving data that might be stale or foreign.
 *
 * An entry also matches when it is identical to an admitted file except for its line-ending style
 * (CRLF vs LF): that is a benign re-serialization performed by some bundle generators, not a content
 * difference, so it is not treated as a mismatch. Any other difference still fails verification.
 */
export function parseNavigatorBundle(
  bytes: Uint8Array,
  bundleIdentity: NavigatorBundleIdentity,
  admittedBpmnHashes: AdmittedBpmnHashes,
): NavigatorParseResult {
  const text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  const match = WRAPPER_PATTERN.exec(text);
  if (!match) {
    throw new ProcessLandscapeError("invalid-navigator-wrapper", `${bundleIdentity.path}: navigator-data bundle does not match the exact "window.NAV_DATA = ...;" wrapper`);
  }

  let parsed: unknown;
  try {
    parsed = parseStrictJson(match[1]!);
  } catch (error) {
    throw new ProcessLandscapeError("invalid-navigator-json", `${bundleIdentity.path}: ${error instanceof Error ? error.message : "invalid JSON"}`);
  }
  if (!isPlainObject(parsed)) {
    throw new ProcessLandscapeError("invalid-navigator-shape", `${bundleIdentity.path}: navigator-data bundle must decode to a JSON object`);
  }

  const diagnostics: LandscapeDiagnostic[] = [];
  const xmlMap = parsed.xml;
  if (!isPlainObject(xmlMap)) {
    throw new ProcessLandscapeError("invalid-navigator-shape", `${bundleIdentity.path}: navigator-data bundle must declare an "xml" map`);
  }

  let mismatchCount = 0;
  let index = 0;
  for (const value of Object.values(xmlMap)) {
    if (typeof value !== "string") {
      throw new ProcessLandscapeError("invalid-navigator-shape", `${bundleIdentity.path}: every navigator-data "xml" entry must be a string`);
    }
    const entrySha256 = createHash("sha256").update(value, "utf8").digest("hex");
    const entryMatches = admittedBpmnHashes.raw.has(entrySha256)
      || admittedBpmnHashes.newlineNormalized.has(newlineNormalizedSha256(value));
    if (!entryMatches) {
      mismatchCount++;
      diagnostics.push({
        code: "navigator-xml-mismatch",
        message: "A navigator-data \"xml\" entry does not match the raw bytes of any admitted BPMN input",
        disposition: "unresolved",
        source: { path: bundleIdentity.path, ref: `xml-entry-${index}` },
      });
    }
    index++;
  }

  const verified = mismatchCount === 0;
  const navigator: ProcessLandscapeNavigator = { bundle: bundleIdentity, verified };

  if (!verified) {
    // Never silently serve possibly stale/foreign navigator view data once verification fails.
    return { navigator, diagnostics };
  }

  for (const category of CATEGORY_NAMES) {
    const sourceKey = CATEGORY_SOURCE_KEYS[category];
    const value = (parsed as Record<string, unknown>)[sourceKey];
    if (value === undefined) continue;
    if (!validCategory(category, value)) {
      diagnostics.push({
        code: "invalid-navigator-category-shape",
        message: `The navigator-data "${sourceKey}" entry does not match its category contract or safe local file-reference bounds`,
        disposition: "partial",
        source: { path: bundleIdentity.path, ref: sourceKey },
      });
      continue;
    }
    navigator[category] = value;
  }

  // `generated` is a provenance/version marker (e.g. a build timestamp), not a data collection, so
  // it is copied through verbatim without category shape restrictions.
  if (parsed.generated !== undefined) navigator.generated = parsed.generated;

  return { navigator, diagnostics };
}
