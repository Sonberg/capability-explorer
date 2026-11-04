import { createHash, timingSafeEqual } from "node:crypto";
import { constants } from "node:fs";
import { lstat, open, readdir } from "node:fs/promises";
import { dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { validateArtifact } from "../contracts/validateArtifact.ts";
import { validateProfileSchema } from "./profileSchemas.ts";
import { canonicalContentBytes, canonicalizationRecipe } from "./canonicalContent.ts";
import { parseStrictJson } from "./strictJson.ts";
import { sourceBytes, type ModelSource } from "../models/source.ts";

const profiles = new Set(["ce.v1", "ce.models.v1", "ce.candidates.v1", "ce.model-source.v1"]);
const maxManifestBytes = 1_048_576;
const maxAttemptBytes = 16_384;
const maxArtifactBytes = 16_777_216;
const maxTotalBytes = 134_217_728;
const maxInputs = 128;
const maxRepositories = 32;

const contentKeys = new Set([
  "content", "rawxml", "xml", "source", "sourcetext", "rawsource", "expression", "expressions",
  "code", "snippet", "sourcecode", "rawpredicate", "literals", "symbols",
]);
const forbiddenKeys = new Set([
  "password", "token", "secret", "authorization", "accesstoken", "refreshtoken",
  "author", "authoremail", "committer", "committeremail", "gitauthor",
]);

export interface CollectionProblem { message: string }
export interface CollectionValidationResult {
  ok: boolean;
  contentSha256?: string;
  problems: CollectionProblem[];
}

interface Repository {
  repositoryId: string;
  providerId: string;
  producerId: string;
  tenantRef: string;
  rightsRef: string;
  commitSha: string;
  treeSha: string;
  permittedContentProfiles: string[];
}

interface Input {
  repositoryId: string;
  profile: string;
  unitPath: string;
  required: boolean;
  status: string;
  artifactPath: string | null;
  sha256: string | null;
  byteLength: number;
  contentProfile: string | null;
}

interface Content {
  schema: string;
  canonicalization: string;
  recipeSha256: string;
  configSha256: string;
  policySha256: string;
  repositories: Repository[];
  inputs: Input[];
}

interface Envelope { schema: string; contentSha256: string; content: Content }

export async function validateCollectionBundle(bundleDirectory: string): Promise<CollectionValidationResult> {
  const problems: CollectionProblem[] = [];
  try {
    const root = resolve(bundleDirectory);
    if (!await isRealDirectory(root)) return invalid("bundle directory is missing or uses a symbolic link");
    const descriptorBytes = await readBounded(join(root, "collection.json"), maxManifestBytes);
    const attemptBytes = await readBounded(join(root, "attempt.json"), maxAttemptBytes);
    const envelope = parseJson(descriptorBytes) as Envelope;
    const attempt = parseJson(attemptBytes) as Record<string, unknown>;
    if (!isObject(envelope) || !onlyKeys(envelope, ["schema", "contentSha256", "content"]) ||
        envelope.schema !== "ce.collection.v1" || !isSha256(envelope.contentSha256) || !isObject(envelope.content)) {
      return invalid("bundle descriptor is invalid");
    }
    const schemaProblems = validateProfileSchema("ce.collection.v1", envelope);
    if (schemaProblems.length) return {
      ok: false, problems: schemaProblems.map(message => ({ message: `bundle descriptor ${message}` })),
    };

    if (!onlyKeys(attempt, ["attemptId", "observedAt"]) || typeof attempt.attemptId !== "string" ||
        !/^[a-f0-9]{32}$/.test(attempt.attemptId) || typeof attempt.observedAt !== "string" ||
        !Number.isFinite(Date.parse(attempt.observedAt))) {
      return invalid("attempt metadata is invalid");
    }

    const shapeProblems = validateContentShape(envelope.content);
    if (shapeProblems.length) return { ok: false, problems: shapeProblems };
    const canonical = canonicalContent(envelope.content);
    const digest = sha256(canonical);
    if (!equalSha256(envelope.contentSha256, digest)) return invalid("collection content digest does not match canonical content");

    const repositories = new Map(envelope.content.repositories.map((repo) => [repo.repositoryId, repo]));
    const referenced = new Set<string>();
    const products = new Map<string, { bytes: Uint8Array; input: Input }>();
    let totalBytes = 0;
    for (const [index, input] of envelope.content.inputs.entries()) {
      if (input.status === "optional-failed") continue;
      const remainingBytes = maxTotalBytes - totalBytes;
      if (remainingBytes <= 0 || input.byteLength > remainingBytes) {
        problems.push({ message: "aggregate read budget exhausted" });
        continue;
      }
      const repo = repositories.get(input.repositoryId)!;
      const artifactPath = input.artifactPath!;
      const file = resolveArtifact(root, artifactPath);
      if (!file || !await isRegularFile(file)) {
        problems.push({ message: "referenced artifact is missing or unsafe" });
        continue;
      }

      const expectedPath = `artifacts/${input.repositoryId}/${input.profile}/${index.toString().padStart(4, "0")}.json`;
      if (artifactPath !== expectedPath) {
        problems.push({ message: "artifact references are not deterministic" });
        continue;
      }

      const bytes = await readBounded(file, Math.min(maxArtifactBytes, remainingBytes));
      totalBytes += bytes.byteLength;
      if (bytes.byteLength !== input.byteLength || totalBytes > maxTotalBytes ||
          sha256(bytes) !== input.sha256) {
        problems.push({ message: "artifact bytes do not match descriptor length or SHA-256" });
        continue;
      }

      problems.push(...validateProfile(input.profile, bytes, repo, input.unitPath,
        input.contentProfile, input.repositoryId).map((message) => ({ message })));
      referenced.add(artifactPath);
      products.set(`${input.repositoryId}\n${input.profile}\n${input.unitPath}`, { bytes, input });
    }

    for (const { bytes, input } of products.values()) {
      if (input.profile !== "ce.model-source.v1") continue;
      try {
        const source = parseJson(bytes) as ModelSource;
        const companion = products.get(`${input.repositoryId}\nce.models.v1\n${input.unitPath}`);
        if (!companion || sha256(companion.bytes) !== source.modelArtifactSha256) throw new Error("companion missing or digest mismatch");
        const evidence = parseJson(companion.bytes) as Record<string, unknown>;
        if (evidence.sourceSha256 !== source.sourceSha256 || evidence.repositoryId !== source.repositoryId ||
            evidence.commitSha !== source.commitSha || evidence.unitPath !== source.unitPath) throw new Error("companion source identity mismatch");
      } catch { problems.push({ message: "source companion binding is invalid" }); }
    }

    const files = await listFilesWithoutLinks(root);
    for (const path of files) {
      const name = relative(root, path).split(sep).join("/");
      if (name !== "collection.json" && name !== "attempt.json" && !referenced.has(name)) {
        problems.push({ message: "bundle contains an unreferenced file" });
      }
    }
    return problems.length ? { ok: false, problems: problems.slice(0, 32) } : { ok: true, contentSha256: digest, problems: [] };
  } catch (error) {
    if (error instanceof SyntaxError) return invalid("bundle JSON is invalid or ambiguous");
    if (error instanceof Error && error.message.startsWith("strict mode:"))
      return invalid(`profile schema configuration failed: ${error.message.slice(0, 200)}`);
    return invalid(error instanceof Error && error.message === "cancelled" ? "validation cancelled" : "bundle validation failed");
  }
}

function validateContentShape(content: Content): CollectionProblem[] {
  const problems: CollectionProblem[] = [];
  if (!onlyKeys(content, ["schema", "canonicalization", "recipeSha256", "configSha256", "policySha256", "repositories", "inputs"]) ||
      content.schema !== "ce.collection.v1" || !isSha256(content.recipeSha256) ||
      content.canonicalization !== canonicalizationRecipe ||
      !isSha256(content.configSha256) || !isSha256(content.policySha256) ||
      !Array.isArray(content.repositories) || content.repositories.length < 1 ||
      content.repositories.length > maxRepositories || !Array.isArray(content.inputs) ||
      content.inputs.length < 1 || content.inputs.length > maxInputs) {
    return [{ message: "collection identity or count is outside profile bounds" }];
  }

  const ids = new Set<string>();
  for (const repository of content.repositories) {
    if (!isObject(repository) ||
        !onlyKeys(repository, ["repositoryId", "providerId", "producerId", "tenantRef", "rightsRef",
          "commitSha", "treeSha", "permittedContentProfiles"]) ||
        !isRepositoryId(repository.repositoryId) || !isSafeRef(repository.providerId) ||
        !isId(repository.producerId) || !isId(repository.tenantRef) || !isId(repository.rightsRef) ||
        !isCommitSha(repository.commitSha) || !isCommitSha(repository.treeSha) ||
        !Array.isArray(repository.permittedContentProfiles) ||
        repository.permittedContentProfiles.some((profile) => !isId(profile)) ||
        !isSortedUnique(repository.permittedContentProfiles) || ids.has(repository.repositoryId)) {
      problems.push({ message: "repository metadata is invalid or duplicated" });
    } else {
      ids.add(repository.repositoryId);
    }
  }

  const unique = new Set<string>();
  for (const input of content.inputs) {
    if (!isObject(input) ||
        !onlyKeys(input, ["repositoryId", "profile", "unitPath", "required", "status", "artifactPath",
          "sha256", "byteLength", "contentProfile"]) ||
        typeof input.repositoryId !== "string" || !ids.has(input.repositoryId) ||
        typeof input.profile !== "string" || typeof input.unitPath !== "string" || !isRelativePath(input.unitPath) ||
        typeof input.required !== "boolean" ||
        (input.contentProfile !== null && !isId(input.contentProfile))) {
      problems.push({ message: "input identity is invalid" });
      continue;
    }

    const key = `${input.repositoryId}\n${input.profile}\n${input.unitPath}`;
    if (unique.has(key)) problems.push({ message: "duplicate input identity" });
    unique.add(key);

    if (input.status === "included") {
      if (!profiles.has(input.profile) || !isArtifactPath(input.artifactPath) ||
          !isSha256(input.sha256) || !isBoundedLength(input.byteLength)) {
        problems.push({ message: "included artifact descriptor is invalid" });
      }
    } else if (input.status === "optional-failed") {
      if (input.required || (!profiles.has(input.profile) && !isId(input.profile)) ||
          input.artifactPath !== null || !isSha256(input.sha256) || !isBoundedLength(input.byteLength)) {
        problems.push({ message: "optional failure disposition is invalid" });
      }
    } else {
      problems.push({ message: "unknown input disposition" });
    }
  }

  if (!isSortedBy(content.repositories, (value) => value.repositoryId) ||
      !isSortedBy(content.inputs, (value) => `${value.repositoryId}\n${value.profile}\n${value.unitPath}`)) {
    problems.push({ message: "repositories and inputs must be deterministically sorted" });
  }
  return problems;
}

function canonicalContent(content: Content): string {
  return canonicalContentBytes({
    schema: content.schema,
    canonicalization: content.canonicalization,
    recipeSha256: content.recipeSha256,
    configSha256: content.configSha256,
    policySha256: content.policySha256,
    repositories: content.repositories.map((repo) => ({
      repositoryId: repo.repositoryId,
      providerId: repo.providerId,
      producerId: repo.producerId,
      tenantRef: repo.tenantRef,
      rightsRef: repo.rightsRef,
      commitSha: repo.commitSha,
      treeSha: repo.treeSha,
      permittedContentProfiles: [...repo.permittedContentProfiles],
    })),
    inputs: content.inputs.map((input) => ({
      repositoryId: input.repositoryId,
      profile: input.profile,
      unitPath: input.unitPath,
      required: input.required,
      status: input.status,
      artifactPath: input.artifactPath,
      sha256: input.sha256,
      byteLength: input.byteLength,
      contentProfile: input.contentProfile,
    })),
  });
}

function validateProfile(
  profile: string,
  bytes: Uint8Array,
  repository: Repository,
  unitPath: string,
  contentProfile: string | null,
  repositoryId: string,
): string[] {
  const errors: string[] = [];
  let text: string;
  try {
    text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch {
    return ["artifact is not valid UTF-8"];
  }

  let root: unknown;
  if (profile === "ce.v1") {
    const result = validateArtifact(text);
    if (!result.ok || !result.header) {
      errors.push(...result.problems.slice(0, 8).map((problem) => `ce.v1 line ${problem.line}: ${problem.message}`));
    } else if (result.header.repositoryId !== repositoryId ||
        result.header.commitSha !== repository.commitSha || result.header.unit.path !== unitPath) {
      errors.push("ce.v1 header identity does not match the collection input");
    }
    root = text.split("\n").filter(line => line.length > 0).map(parseStrictJson);
  } else {
    try {
      root = parseStrictJson(text);
      if (!isObject(root)) return ["profile root must be a JSON object"];
      errors.push(...validateProfileSchema(profile, root));
      if (profile === "ce.model-source.v1") {
        if (root.repositoryId !== repositoryId || root.commitSha !== repository.commitSha || root.unitPath !== unitPath) {
          errors.push("source identity does not match collection");
        }
        try { sourceBytes(root as unknown as ModelSource); }
        catch { errors.push("source bytes, digest, XML or limits are invalid"); }
        if (contentProfile !== "model-content" || !repository.permittedContentProfiles.includes("model-content")) {
          errors.push("source requires matching explicitly permitted contentProfile");
        }
        return errors;
      }
      const expectedPayload = profile === "ce.models.v1" ? "elements" : "candidates";
      const allowedKeys = profile === "ce.models.v1"
        ? ["schema", "repositoryId", "commitSha", "unitPath", "sourceSha256", "contentProfile",
          "coverage", "diagnostics", "elements", "relationships"]
        : ["schema", "recipe", "repositoryId", "commitSha", "unitPath", "sourceSha256",
          "coverage", "diagnostics", "candidates"];
      if (root.schema !== profile || root.repositoryId !== repositoryId ||
          root.commitSha !== repository.commitSha || root.unitPath !== unitPath ||
          !isSha256(root.sourceSha256) || !isObject(root.coverage) ||
          (root.coverage.status !== "full" && root.coverage.status !== "partial") ||
          !Array.isArray(root.diagnostics) || !Array.isArray(root[expectedPayload]) ||
          Object.keys(root).some((key) => !allowedKeys.includes(key))) {
        errors.push(`${profile} common identity or required arrays are invalid`);
      }
      if (profile === "ce.models.v1") {
        if (root.contentProfile !== "structural" && root.contentProfile !== "model-content") {
          errors.push("ce.models.v1 contentProfile is unsupported");
        }
        if (!isObject(root.coverage) || !Array.isArray(root.coverage.supported) ||
            !Array.isArray(root.coverage.limitations) ||
            Object.keys(root.coverage).some((key) => !["status", "supported", "limitations"].includes(key))) {
          errors.push("ce.models.v1 coverage is invalid");
        }
        if (!Array.isArray(root.relationships)) errors.push("ce.models.v1 relationships must be an array");
      } else {
        if (root.recipe !== "roslyn.scalar-guards.v1" || !isObject(root.coverage) ||
            root.coverage.scope !== "single-file-intra-method" ||
            !Number.isSafeInteger(root.coverage.supportedMethods) || !Number.isSafeInteger(root.coverage.unsupportedMethods) ||
            Object.keys(root.coverage).some((key) => !["status", "scope", "supportedMethods", "unsupportedMethods"].includes(key))) {
          errors.push("ce.candidates.v1 recipe or bounded coverage is invalid");
        }
      }
    } catch (error) {
      if (error instanceof SyntaxError) return ["artifact is not valid JSON"];
      throw error;
    }
  }

  const scan = scanPrivacy(root);
  if (scan.forbidden) errors.push("credential or personal Git author data is not exportable");
  const requiredProfile = profile === "ce.v1" ? "native-content"
    : profile === "ce.models.v1" ? "model-content" : "candidate-content";
  if (contentProfile !== requiredProfile || !repository.permittedContentProfiles.includes(requiredProfile)) {
    errors.push("artifact requires its matching explicitly permitted contentProfile");
  }
  return errors;
}

function scanPrivacy(value: unknown, depth = 0): { content: boolean; forbidden: boolean } {
  if (depth > 32) return { content: false, forbidden: true };
  if (Array.isArray(value)) {
    return value.reduce((result, item) => merge(result, scanPrivacy(item, depth + 1)), { content: false, forbidden: false });
  }
  if (!isObject(value)) return { content: false, forbidden: false };
  let result = { content: false, forbidden: false };
  for (const [key, child] of Object.entries(value)) {
    if (child !== null && contentKeys.has(key.toLowerCase())) result.content = true;
    if (child !== null && forbiddenKeys.has(key.toLowerCase())) result.forbidden = true;
    result = merge(result, scanPrivacy(child, depth + 1));
  }
  return result;
}

function merge(left: { content: boolean; forbidden: boolean }, right: { content: boolean; forbidden: boolean }) {
  return { content: left.content || right.content, forbidden: left.forbidden || right.forbidden };
}

function resolveArtifact(root: string, value: string): string | undefined {
  if (!isArtifactPath(value)) return undefined;
  const file = resolve(root, ...value.split("/"));
  const fromRoot = relative(root, file);
  return fromRoot.startsWith(`artifacts${sep}`) && !isAbsolute(fromRoot) ? file : undefined;
}

async function readBounded(path: string, limit: number): Promise<Buffer> {
  if (!await isRegularFile(path)) throw new Error("unsafe file");
  const file = await open(path, constants.O_RDONLY | constants.O_NOFOLLOW);
  try {
    const stat = await file.stat();
    if (!stat.isFile() || stat.size < 1 || stat.size > limit) throw new Error("file exceeds size bounds");
    const bytes = Buffer.alloc(stat.size + 1);
    let length = 0;
    while (length < bytes.length) {
      const { bytesRead } = await file.read(bytes, length, bytes.length - length);
      if (bytesRead === 0) break;
      length += bytesRead;
    }
    if (length !== stat.size) throw new Error("file changed while reading");
    return bytes.subarray(0, length);
  } finally {
    await file.close();
  }
}

async function isRealDirectory(path: string): Promise<boolean> {
  try {
    const stat = await lstat(path);
    return stat.isDirectory() && !stat.isSymbolicLink();
  } catch {
    return false;
  }
}

async function isRegularFile(path: string): Promise<boolean> {
  try {
    const stat = await lstat(path);
    return stat.isFile() && !stat.isSymbolicLink();
  } catch {
    return false;
  }
}

async function listFilesWithoutLinks(root: string): Promise<string[]> {
  const output: string[] = [];
  const pending = [root];
  let entries = 0;
  while (pending.length) {
    const current = pending.pop()!;
    for (const entry of await readdir(current, { withFileTypes: true })) {
      if (++entries > maxInputs * 4 + maxRepositories + 4) throw new Error("bundle contains too many entries");
      const path = join(current, entry.name);
      if (entry.isSymbolicLink()) throw new Error("symbolic link in bundle");
      if (entry.isDirectory()) pending.push(path);
      else if (entry.isFile()) output.push(path);
      else throw new Error("unsupported bundle filesystem entry");
      if (output.length > maxInputs + 2) throw new Error("bundle contains too many files");
    }
  }
  return output;
}

function parseJson(bytes: Uint8Array): unknown {
  return parseStrictJson(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function onlyKeys(value: object, keys: string[]): boolean {
  const allowed = new Set(keys);
  return Object.keys(value).every((key) => allowed.has(key));
}

function isSha256(value: unknown): value is string { return typeof value === "string" && /^[a-f0-9]{64}$/.test(value); }
function isCommitSha(value: unknown): value is string { return typeof value === "string" && /^[a-f0-9]{40}$/.test(value); }
function isRepositoryId(value: unknown): value is string { return typeof value === "string" && /^[a-z0-9][a-z0-9._-]{0,99}$/.test(value); }
function isId(value: unknown): value is string { return typeof value === "string" && /^[a-z0-9][a-z0-9._:-]{0,127}$/.test(value); }
function isSafeRef(value: unknown): value is string { return typeof value === "string" && /^[A-Za-z0-9._:-]{1,128}$/.test(value); }
function isBoundedLength(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value > 0 && value <= maxArtifactBytes;
}
function isArtifactPath(value: unknown): value is string {
  return typeof value === "string" && value.startsWith("artifacts/") &&
    value.endsWith(".json") && isRelativePath(value);
}
function isRelativePath(value: string): boolean {
  return value.length > 0 && value.length <= 1024 && !value.startsWith("/") && !value.includes("\\") &&
    !value.includes(":") && !/[\u0000-\u001f\u007f]/.test(value) &&
    value.split("/").every((part) => part.length > 0 && part !== "." && part !== "..");
}
function isSortedUnique(values: string[]): boolean {
  return values.every((value, index) => index === 0 || values[index - 1]! < value);
}
function isSortedBy<T>(values: T[], key: (value: T) => string): boolean {
  return values.every((value, index) => index === 0 || key(values[index - 1]!) <= key(value));
}
function sha256(value: string | Uint8Array): string {
  return createHash("sha256").update(value).digest("hex");
}
function equalSha256(left: string, right: string): boolean {
  return timingSafeEqual(Buffer.from(left, "hex"), Buffer.from(right, "hex"));
}
function invalid(message: string): CollectionValidationResult { return { ok: false, problems: [{ message }] }; }
