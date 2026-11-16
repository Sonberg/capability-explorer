import assert from "node:assert/strict";
import { mkdir, rm, symlink, writeFile } from "node:fs/promises";
import { readFileSync } from "node:fs";
import { randomUUID } from "node:crypto";
import { join, resolve } from "node:path";
import test from "node:test";
import { Ajv2020 } from "ajv/dist/2020.js";
import addFormatsModule from "ajv-formats";
import { buildProcessLandscapeSnapshot } from "../../src/models/process-landscape.ts";
import { ProcessLandscapeError } from "../../src/models/process-landscape-types.ts";

const addFormats = ((addFormatsModule as unknown as { default?: unknown }).default ?? addFormatsModule) as (ajv: Ajv2020) => Ajv2020;

const bpmn = (processId: string, taskId = "review") => `<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" id="defs-${processId}" targetNamespace="urn:landscape">
  <process id="${processId}" name="Sample ${processId}">
    <startEvent id="start-${processId}"/>
    <task id="${taskId}"/>
    <endEvent id="end-${processId}"/>
    <sequenceFlow id="flow-1-${processId}" sourceRef="start-${processId}" targetRef="${taskId}"/>
    <sequenceFlow id="flow-2-${processId}" sourceRef="${taskId}" targetRef="end-${processId}"/>
  </process>
</definitions>
`;

async function makeWorkspace(): Promise<string> {
  const root = resolve(".sandbox-cache", "process-landscape", randomUUID());
  await mkdir(root, { recursive: true });
  return root;
}

const manifest = (entries: Array<{ path: string; role: string }>, repositoryId = "local:order-intake") => ({
  schema: "ce.process-landscape-manifest.v1",
  manifestVersion: 1,
  repositoryId,
  entries,
});

test("Given an admitted manifest, the snapshot identity is deterministic and sensitive to content changes", async () => {
  const root = await makeWorkspace();
  try {
    await writeFile(join(root, "intake.bpmn"), bpmn("intake"));
    const input = manifest([{ path: "intake.bpmn", role: "bpmn" }]);

    const first = await buildProcessLandscapeSnapshot(root, input);
    const second = await buildProcessLandscapeSnapshot(root, input);
    assert.equal(first.snapshotId, second.snapshotId, "identical inputs must produce the same snapshot id");
    assert.match(first.snapshotId, /^landscape:[a-f0-9]{64}$/);
    assert.equal(first.coverage.status, "full");
    assert.equal(first.processes.length, 1);
    assert.equal(first.processes[0]?.processId, "intake");
    assert.equal(first.processes[0]?.name, "Sample intake");
    assert.doesNotMatch(JSON.stringify(first), /commitSha/i, "process-landscape output must never serialize a commitSha");

    await writeFile(join(root, "intake.bpmn"), bpmn("intake", "review-2"));
    const changed = await buildProcessLandscapeSnapshot(root, input);
    assert.notEqual(changed.snapshotId, first.snapshotId, "content changes must change the snapshot id");
    assert.equal(changed.processes[0]?.processIdentity, first.processes[0]?.processIdentity, "stable element identity is keyed by repository+path, not content");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given reordered manifest entries, the snapshot identity does not depend on manifest entry order", async () => {
  const root = await makeWorkspace();
  try {
    await writeFile(join(root, "a.bpmn"), bpmn("alpha"));
    await writeFile(join(root, "b.bpmn"), bpmn("beta"));
    const forward = manifest([{ path: "a.bpmn", role: "bpmn" }, { path: "b.bpmn", role: "bpmn" }]);
    const reverse = manifest([{ path: "b.bpmn", role: "bpmn" }, { path: "a.bpmn", role: "bpmn" }]);

    const first = await buildProcessLandscapeSnapshot(root, forward);
    const second = await buildProcessLandscapeSnapshot(root, reverse);
    assert.equal(first.snapshotId, second.snapshotId);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given manifest path and role violations, the producer rejects the input before publishing output", async () => {
  const root = await makeWorkspace();
  try {
    await writeFile(join(root, "intake.bpmn"), bpmn("intake"));

    await assert.rejects(buildProcessLandscapeSnapshot(root, manifest([{ path: "/etc/intake.bpmn", role: "bpmn" }])), ProcessLandscapeError);
    await assert.rejects(buildProcessLandscapeSnapshot(root, manifest([{ path: "../intake.bpmn", role: "bpmn" }])), ProcessLandscapeError);
    await assert.rejects(buildProcessLandscapeSnapshot(root, manifest([{ path: "a/../intake.bpmn", role: "bpmn" }])), ProcessLandscapeError);
    await assert.rejects(buildProcessLandscapeSnapshot(root, manifest([
      { path: "intake.bpmn", role: "bpmn" }, { path: "intake.bpmn", role: "bpmn" },
    ])), ProcessLandscapeError, "duplicate paths must be rejected");
    await assert.rejects(buildProcessLandscapeSnapshot(root, manifest([{ path: "intake.unknown", role: "bpmn" }])), ProcessLandscapeError, "unsupported extension must be rejected");
    await assert.rejects(buildProcessLandscapeSnapshot(root, manifest([{ path: "intake.bpmn", role: "spreadsheet" }])), ProcessLandscapeError, "unsupported role must be rejected");
    await assert.rejects(buildProcessLandscapeSnapshot(root, { ...manifest([]), entries: [] }), ProcessLandscapeError, "empty manifests must be rejected");
    await assert.rejects(buildProcessLandscapeSnapshot(root, { schema: "ce.process-landscape-manifest.v1", manifestVersion: 2, repositoryId: "x", entries: [] }), ProcessLandscapeError, "unknown manifest version must be rejected");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given a symlink path component, the producer rejects the manifest entry", async () => {
  const root = await makeWorkspace();
  try {
    const nested = join(root, "nested");
    await mkdir(nested, { recursive: true });
    await writeFile(join(nested, "intake.bpmn"), bpmn("intake"));

    await symlink(join(root, "intake.bpmn"), join(root, "linked.bpmn"));
    await assert.rejects(
      buildProcessLandscapeSnapshot(root, manifest([{ path: "linked.bpmn", role: "bpmn" }])),
      ProcessLandscapeError,
      "a symlinked file must be rejected",
    );

    await symlink(nested, join(root, "linked-dir"));
    await assert.rejects(
      buildProcessLandscapeSnapshot(root, manifest([{ path: "linked-dir/intake.bpmn", role: "bpmn" }])),
      ProcessLandscapeError,
      "a symlinked directory component must be rejected",
    );
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given malformed or oversized XML, the producer rejects the BPMN entry explicitly", async () => {
  const root = await makeWorkspace();
  try {
    await writeFile(join(root, "broken.bpmn"), "<definitions><process id=\"p\"></definitions>");
    // Malformed XML is rejected by the shared xml.ts parser (ModelXmlError), reused as-is rather than rewrapped.
    await assert.rejects(buildProcessLandscapeSnapshot(root, manifest([{ path: "broken.bpmn", role: "bpmn" }])), /malformed/, "malformed XML must be rejected");

    await writeFile(join(root, "oversized.bpmn"), Buffer.alloc(5 * 1024 * 1024, "a"));
    await assert.rejects(buildProcessLandscapeSnapshot(root, manifest([{ path: "oversized.bpmn", role: "bpmn" }])), ProcessLandscapeError, "oversized files must be rejected");

    await writeFile(join(root, "invalid-utf8.bpmn"), Buffer.from([0xff, 0xfe, 0x00, 0x01]));
    await assert.rejects(buildProcessLandscapeSnapshot(root, manifest([{ path: "invalid-utf8.bpmn", role: "bpmn" }])), ProcessLandscapeError, "invalid UTF-8 must be rejected");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given the explicit model-content permission, the producer embeds raw bytes only when granted", async () => {
  const root = await makeWorkspace();
  try {
    const xml = bpmn("intake");
    await writeFile(join(root, "intake.bpmn"), xml);
    const input = manifest([{ path: "intake.bpmn", role: "bpmn" }]);

    const structural = await buildProcessLandscapeSnapshot(root, input);
    assert.equal(structural.contentProfile, "structural");
    assert.equal(structural.sources[0]?.sourceBytesBase64, undefined, "default profile must not embed source bytes");

    const withContent = await buildProcessLandscapeSnapshot(root, input, { contentProfile: "model-content" });
    assert.equal(withContent.contentProfile, "model-content");
    assert.deepEqual(Buffer.from(withContent.sources[0]!.sourceBytesBase64!, "base64"), Buffer.from(xml, "utf8"));
    assert.equal(withContent.processes[0]?.path, withContent.sources[0]?.path, "process entries reference the shared source by path/hash rather than embedding bytes");
    assert.equal((withContent.processes[0] as unknown as { sourceBytesBase64?: string }).sourceBytesBase64, undefined, "raw bytes must live only in the top-level sources[], never on the process entry");

    await assert.rejects(buildProcessLandscapeSnapshot(root, input, { contentProfile: "other" as "model-content" }), ProcessLandscapeError, "invalid content profile values must be rejected");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given explicit process metadata, proposal status is preserved and never inferred", async () => {
  const root = await makeWorkspace();
  try {
    await writeFile(join(root, "intake.bpmn"), bpmn("intake"));
    await writeFile(join(root, "approved.bpmn"), bpmn("approved-proc"));
    await writeFile(join(root, "metadata.json"), JSON.stringify({
      schema: "ce.process-landscape-metadata.v1",
      processes: {
        intake: { proposalStatus: "proposed", contextTags: ["front-office"], phaseTags: ["discovery"] },
        "unknown-process": { proposalStatus: "approved" },
      },
    }));
    const input = manifest([
      { path: "intake.bpmn", role: "bpmn" },
      { path: "approved.bpmn", role: "bpmn" },
      { path: "metadata.json", role: "process-metadata" },
    ]);

    const snapshot = await buildProcessLandscapeSnapshot(root, input);
    const intake = snapshot.processes.find((process) => process.processId === "intake");
    const approved = snapshot.processes.find((process) => process.processId === "approved-proc");
    assert.equal(intake?.proposalStatus, "proposed");
    assert.deepEqual(intake?.contextTags, ["front-office"]);
    assert.equal(approved?.proposalStatus, undefined, "proposal status must never be inferred for processes without explicit metadata");
    assert.ok(snapshot.diagnostics.some((diagnostic) => diagnostic.code === "unresolved-process-metadata"), "metadata referencing an unknown process id must be preserved as a diagnostic");
    assert.equal(snapshot.coverage.status, "partial");
    assert.deepEqual(snapshot.overlays.context, [{ tag: "front-office", processIdentities: [intake!.processIdentity] }]);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given unresolved cross-process references, the snapshot preserves them as explicit diagnostics rather than failing", async () => {
  const root = await makeWorkspace();
  try {
    await writeFile(join(root, "intake.bpmn"), bpmn("intake"));
    await writeFile(join(root, "events.json"), JSON.stringify({
      schema: "ce.process-landscape-events.v1",
      events: [
        { eventId: "evt-1", source: { processId: "intake", elementId: "review" }, target: { processId: "missing-process" } },
      ],
    }));
    await writeFile(join(root, "lifecycle.json"), JSON.stringify({
      schema: "ce.process-landscape-lifecycle.v1",
      objects: [{ objectId: "order", stages: [{ stage: "created", order: 0, processId: "intake", elementId: "missing-element" }] }],
    }));
    const input = manifest([
      { path: "intake.bpmn", role: "bpmn" },
      { path: "events.json", role: "cross-process-events" },
      { path: "lifecycle.json", role: "object-lifecycle" },
    ]);

    const snapshot = await buildProcessLandscapeSnapshot(root, input);
    assert.equal(snapshot.crossProcessEvents[0]?.source.resolved, true);
    assert.equal(snapshot.crossProcessEvents[0]?.target.resolved, false);
    assert.equal(snapshot.objectLifecycle[0]?.stages[0]?.resolved, false);
    assert.ok(snapshot.diagnostics.some((diagnostic) => diagnostic.code === "unresolved-cross-reference"));
    assert.equal(snapshot.coverage.status, "partial");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given the search index, process and element deep links resolve to their identities", async () => {
  const root = await makeWorkspace();
  try {
    await writeFile(join(root, "intake.bpmn"), bpmn("intake"));
    const snapshot = await buildProcessLandscapeSnapshot(root, manifest([{ path: "intake.bpmn", role: "bpmn" }]));
    const processEntry = snapshot.searchIndex.find((entry) => entry.kind === "process");
    assert.equal(processEntry?.deepLink, `process:${snapshot.processes[0]!.processIdentity}`);
    const elementEntry = snapshot.searchIndex.find((entry) => entry.kind === "element" && entry.term === "review");
    assert.equal(elementEntry?.deepLink, `process:${snapshot.processes[0]!.processIdentity}#${elementEntry?.elementIdentity}`);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given duplicate BPMN process ids across admitted files, ambiguity is reported explicitly rather than last-wins", async () => {
  const root = await makeWorkspace();
  try {
    await writeFile(join(root, "a.bpmn"), bpmn("dup", "review-a"));
    await writeFile(join(root, "b.bpmn"), bpmn("dup", "review-b"));
    await writeFile(join(root, "metadata.json"), JSON.stringify({
      schema: "ce.process-landscape-metadata.v1",
      processes: { dup: { proposalStatus: "approved" } },
    }));
    await writeFile(join(root, "events.json"), JSON.stringify({
      schema: "ce.process-landscape-events.v1",
      events: [{ eventId: "evt-1", source: { processId: "dup" }, target: { processId: "dup" } }],
    }));
    const input = manifest([
      { path: "a.bpmn", role: "bpmn" },
      { path: "b.bpmn", role: "bpmn" },
      { path: "metadata.json", role: "process-metadata" },
      { path: "events.json", role: "cross-process-events" },
    ]);

    const snapshot = await buildProcessLandscapeSnapshot(root, input);
    assert.equal(snapshot.processes.length, 2, "both admitted files must still produce separate process entries");
    assert.ok(snapshot.diagnostics.some((d) => d.code === "ambiguous-process-id"), "the duplicate process id must be reported as an explicit diagnostic");
    assert.ok(snapshot.diagnostics.some((d) => d.code === "ambiguous-process-metadata"), "metadata keyed by an ambiguous process id must not be silently applied");
    assert.ok(snapshot.diagnostics.some((d) => d.code === "ambiguous-cross-reference"), "cross references keyed by an ambiguous process id must not be silently resolved");
    assert.ok(snapshot.processes.every((process) => process.proposalStatus === undefined), "ambiguous metadata must never be applied to an arbitrarily chosen process");
    assert.equal(snapshot.crossProcessEvents[0]?.source.resolved, false);
    assert.equal(snapshot.crossProcessEvents[0]?.target.resolved, false);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given a BPMN document declaring multiple processes, each is extracted as a separately navigable process identity sharing path/hash", async () => {
  const root = await makeWorkspace();
  try {
    const multiProcess = `<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" id="defs-multi" targetNamespace="urn:landscape">
  <process id="p1" name="First">
    <startEvent id="s1"/>
    <task id="t1"/>
    <sequenceFlow id="f1" sourceRef="s1" targetRef="t1"/>
  </process>
  <process id="p2" name="Second">
    <startEvent id="s2"/>
    <task id="t2"/>
    <sequenceFlow id="f2" sourceRef="s2" targetRef="t2"/>
  </process>
</definitions>
`;
    await writeFile(join(root, "multi.bpmn"), multiProcess);
    const snapshot = await buildProcessLandscapeSnapshot(root, manifest([{ path: "multi.bpmn", role: "bpmn" }]));

    assert.equal(snapshot.processes.length, 2, "both declared processes must be extracted as separate process entries");
    const [first, second] = snapshot.processes;
    assert.equal(first?.processId, "p1");
    assert.equal(second?.processId, "p2");
    assert.equal(first?.path, second?.path, "process entries from one file share the same path");
    assert.equal(first?.sha256, second?.sha256, "process entries from one file share the same content hash");
    assert.notEqual(first?.processIdentity, second?.processIdentity, "each process must still get a distinct identity");
    assert.match(first!.processIdentity, /^landscape-process:[a-f0-9]{64}$/);
    assert.match(second!.processIdentity, /^landscape-process:[a-f0-9]{64}$/);
    assert.equal(first?.elements.some((element) => element.id === "s2" || element.id === "t2"), false, "elements must be partitioned to their owning process only");
    assert.equal(second?.elements.some((element) => element.id === "s1" || element.id === "t1"), false, "elements must be partitioned to their owning process only");

    // Re-extracting identical bytes must reproduce the exact same per-process identities (stable discriminator).
    const again = await buildProcessLandscapeSnapshot(root, manifest([{ path: "multi.bpmn", role: "bpmn" }]));
    assert.equal(again.processes[0]?.processIdentity, first?.processIdentity);
    assert.equal(again.processes[1]?.processIdentity, second?.processIdentity);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given a multi-process BPMN document using repeated or absent process ids, identities remain distinct via a stable positional discriminator", async () => {
  const root = await makeWorkspace();
  try {
    const repeatedIds = `<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" id="defs-repeat" targetNamespace="urn:landscape">
  <process name="Unnamed one"><startEvent id="s1"/></process>
  <process name="Unnamed two"><startEvent id="s2"/></process>
  <process id="dup" name="Third"><startEvent id="s3"/></process>
</definitions>
`;
    await writeFile(join(root, "repeat.bpmn"), repeatedIds);
    const snapshot = await buildProcessLandscapeSnapshot(root, manifest([{ path: "repeat.bpmn", role: "bpmn" }]));
    assert.equal(snapshot.processes.length, 3);
    const identities = new Set(snapshot.processes.map((process) => process.processIdentity));
    assert.equal(identities.size, 3, "process-less/absent ids must still yield distinct identities");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given a cross-process sequenceFlow in a multi-process document, the relationship is diagnosed explicitly rather than silently dropped or misattributed", async () => {
  const root = await makeWorkspace();
  try {
    const crossing = `<?xml version="1.0" encoding="UTF-8"?>
<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" id="defs-cross" targetNamespace="urn:landscape">
  <process id="p1" name="First">
    <startEvent id="s1"/>
  </process>
  <process id="p2" name="Second">
    <task id="t2"/>
  </process>
  <sequenceFlow id="f-cross" sourceRef="s1" targetRef="t2"/>
</definitions>
`;
    await writeFile(join(root, "cross.bpmn"), crossing);
    const snapshot = await buildProcessLandscapeSnapshot(root, manifest([{ path: "cross.bpmn", role: "bpmn" }]));
    assert.equal(snapshot.processes.length, 2);
    assert.ok(
      snapshot.diagnostics.some((diagnostic) => diagnostic.code === "cross-process-relationship-unresolved"),
      "a relationship spanning two processes must be reported as an explicit diagnostic",
    );
    assert.equal(
      snapshot.processes.flatMap((process) => process.relationships).length,
      0,
      "the unresolved cross-process relationship must not be silently attached to either process",
    );
    assert.equal(snapshot.coverage.status, "partial");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given tags whose locale collation differs from code-unit order, overlay and identity ordering stay locale-independent", async () => {
  const root = await makeWorkspace();
  try {
    await writeFile(join(root, "a.bpmn"), bpmn("alpha"));
    await writeFile(join(root, "b.bpmn"), bpmn("beta"));
    await writeFile(join(root, "metadata.json"), JSON.stringify({
      schema: "ce.process-landscape-metadata.v1",
      processes: {
        alpha: { phaseTags: ["B"] },
        beta: { phaseTags: ["a"] },
      },
    }));
    const input = manifest([
      { path: "a.bpmn", role: "bpmn" },
      { path: "b.bpmn", role: "bpmn" },
      { path: "metadata.json", role: "process-metadata" },
    ]);

    // Locale-aware localeCompare("B", "a") would order ["a", "B"]; ordinal (code-unit) order is ["B", "a"].
    assert.deepEqual(["B", "a"].sort((x, y) => x.localeCompare(y)), ["a", "B"], "test fixture assumption: locale collation diverges from code-unit order");

    const snapshot = await buildProcessLandscapeSnapshot(root, input);
    assert.deepEqual(snapshot.overlays.phase.map((entry) => entry.tag), ["B", "a"], "overlay tag ordering must be code-unit ordinal, not locale-collated");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
