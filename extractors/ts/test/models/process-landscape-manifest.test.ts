import assert from "node:assert/strict";
import { mkdir, mkdtemp, rm, symlink, writeFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";
import { readManifestFile } from "../../src/models/process-landscape-manifest.ts";
import { ProcessLandscapeError } from "../../src/models/process-landscape-types.ts";

async function makeWorkspace(): Promise<string> {
  const root = resolve(".sandbox-cache", "process-landscape-manifest", randomUUID());
  await mkdir(root, { recursive: true });
  return root;
}

test("Given a legitimate nested manifest path, readManifestFile returns its bytes", async () => {
  const root = await makeWorkspace();
  try {
    await mkdir(join(root, "nested"), { recursive: true });
    await writeFile(join(root, "nested", "intake.bpmn"), "legit-content");
    const bytes = await readManifestFile(root, "nested/intake.bpmn", 1024);
    assert.equal(bytes.toString("utf8"), "legit-content");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Given a parent-directory symlink swap race, readManifestFile never returns bytes from outside the base directory", async () => {
  // Outside the base directory entirely, on a separate real temp directory to avoid any ancestor relationship.
  const outside = await mkdtemp(join(tmpdir(), "process-landscape-outside-"));
  const root = await makeWorkspace();
  try {
    await writeFile(join(outside, "intake.bpmn"), "attacker-content");
    const nested = join(root, "nested");

    const legitimateState = async () => {
      await rm(nested, { recursive: true, force: true }).catch(() => undefined);
      await mkdir(nested, { recursive: true });
      await writeFile(join(nested, "intake.bpmn"), "legit-content");
    };
    const swappedState = async () => {
      await rm(nested, { recursive: true, force: true }).catch(() => undefined);
      await symlink(outside, nested);
    };

    await legitimateState();

    let swapping = true;
    const swapLoop = (async () => {
      while (swapping) {
        await swappedState();
        await legitimateState();
      }
    })();

    const attempts = 200;
    const results = await Promise.allSettled(
      Array.from({ length: attempts }, () => readManifestFile(root, "nested/intake.bpmn", 1024)),
    );
    swapping = false;
    await swapLoop;

    for (const result of results) {
      if (result.status === "fulfilled") {
        assert.equal(
          result.value.toString("utf8"),
          "legit-content",
          "a successful read must never return bytes from outside the base directory",
        );
      } else {
        assert.ok(
          result.reason instanceof ProcessLandscapeError,
          "a rejected read during a symlink race must fail with an explicit ProcessLandscapeError",
        );
      }
    }
  } finally {
    await rm(root, { recursive: true, force: true });
    await rm(outside, { recursive: true, force: true });
  }
});

test("Given an already-swapped symlinked directory component, readManifestFile rejects without reading its target", async () => {
  const outside = await mkdtemp(join(tmpdir(), "process-landscape-outside-"));
  const root = await makeWorkspace();
  try {
    await writeFile(join(outside, "secret.bpmn"), "attacker-content");
    await symlink(outside, join(root, "linked"));

    await assert.rejects(
      readManifestFile(root, "linked/secret.bpmn", 1024),
      ProcessLandscapeError,
      "a symlinked directory component must be rejected before its target is ever read",
    );
  } finally {
    await rm(root, { recursive: true, force: true });
    await rm(outside, { recursive: true, force: true });
  }
});

test("Given a missing base directory, readManifestFile fails closed rather than silently resolving relative to cwd", async () => {
  const missingRoot = resolve(".sandbox-cache", "process-landscape-manifest", `missing-${randomUUID()}`);
  await assert.rejects(readManifestFile(missingRoot, "intake.bpmn", 1024), ProcessLandscapeError);
});

test(
  "Given a Windows directory junction masquerading as a manifest path component, readManifestFile rejects it without reading its target",
  { skip: process.platform !== "win32" ? "junction reparse points can only be created and exercised on win32; could not be executed on this platform" : false },
  async () => {
    const outside = await mkdtemp(join(tmpdir(), "process-landscape-outside-"));
    const root = await makeWorkspace();
    try {
      await writeFile(join(outside, "secret.bpmn"), "attacker-content");
      // Windows directory junctions use reparse tag IO_REPARSE_TAG_MOUNT_POINT, distinct from
      // IO_REPARSE_TAG_SYMLINK; `fs.symlink(..., "junction")` is the supported way to create one from
      // Node without administrator privileges, and `lstat().isSymbolicLink()` is not guaranteed to
      // flag it, which is exactly the gap this regression exercises.
      await symlink(outside, join(root, "linked"), "junction");

      await assert.rejects(
        readManifestFile(root, "linked/secret.bpmn", 1024),
        ProcessLandscapeError,
        "a junction directory component must be rejected before its target is ever read",
      );
    } finally {
      await rm(root, { recursive: true, force: true });
      await rm(outside, { recursive: true, force: true });
    }
  },
);
