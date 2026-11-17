import assert from "node:assert/strict";
import fsPromises, { mkdir, rename, rm, symlink, writeFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { syncBuiltinESMExports } from "node:module";
import { join, resolve } from "node:path";
import test from "node:test";
import { readBoundedFile, readManifestFile } from "../../src/models/process-landscape-manifest.ts";
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

const readers = [
  { name: "manifest", read: (root: string) => readManifestFile(root, "nested/intake.bpmn", 1024), raceCode: "manifest-path-race-detected" },
  { name: "bounded", read: (root: string) => readBoundedFile(join(root, "nested/intake.bpmn"), 1024), raceCode: "bounded-file-race-detected" },
];

test("checked file opens report deterministic path races without following symlinks", async context => {
  for (const reader of readers) for (const errno of ["ENOENT", "ENOTDIR", "ELOOP"]) {
    await context.test(`${reader.name}: ${errno} between lstat and open`, {
      skip: errno === "ELOOP" && process.platform === "win32" ? "O_NOFOLLOW is POSIX-only" : false,
    }, async subtest => {
      const root = await makeWorkspace();
      const outside = await makeWorkspace();
      try {
        const nested = join(root, "nested");
        const target = join(nested, "intake.bpmn");
        await mkdir(nested);
        await writeFile(target, "legit-content");
        await writeFile(join(root, "not-a-directory"), "plain-file");
        await writeFile(join(outside, "intake.bpmn"), "attacker-content");
        const originalOpen = fsPromises.open;
        let observedErrno: string | undefined;
        subtest.mock.method(fsPromises, "open", async (...args: Parameters<typeof originalOpen>) => {
          if (errno === "ENOTDIR") {
            await rename(nested, join(root, "parked"));
            await rename(join(root, "not-a-directory"), nested);
          } else {
            await rename(target, join(root, "parked.bpmn"));
            if (errno === "ELOOP") await symlink(join(outside, "intake.bpmn"), target);
          }
          try {
            return await originalOpen(...args);
          } catch (error) {
            observedErrno = (error as NodeJS.ErrnoException).code;
            throw error;
          }
        });
        syncBuiltinESMExports();
        const error: unknown = await reader.read(root).then(() => undefined, error => error);
        assert.equal(observedErrno, errno, "the real filesystem must exercise the intended open errno");
        assert.ok(error instanceof ProcessLandscapeError, `${errno} must not escape as a raw filesystem error`);
        assert.equal(error.code, reader.raceCode);
        assert.equal((error.cause as NodeJS.ErrnoException).code, errno);
      } finally {
        subtest.mock.restoreAll();
        syncBuiltinESMExports();
        await rm(root, { recursive: true, force: true });
        await rm(outside, { recursive: true, force: true });
      }
    });
  }
});

test("unexpected open failures retain their original identity rather than becoming path races", async context => {
  for (const reader of readers) for (const code of ["EIO", "EACCES", "EMFILE", undefined]) {
    await context.test(`${reader.name}: ${code ?? "programming error"}`, async subtest => {
      const root = await makeWorkspace();
      try {
        await mkdir(join(root, "nested"));
        await writeFile(join(root, "nested/intake.bpmn"), "legit-content");
        const failure = code ? Object.assign(new Error("synthetic filesystem failure"), { code }) : new TypeError("synthetic programming error");
        subtest.mock.method(fsPromises, "open", async () => { throw failure; });
        syncBuiltinESMExports();
        await assert.rejects(reader.read(root), error => error === failure);
      } finally {
        subtest.mock.restoreAll();
        syncBuiltinESMExports();
        await rm(root, { recursive: true, force: true });
      }
    });
  }
});

test("Given a parent-directory symlink swap race, readManifestFile never returns bytes from outside the base directory", async () => {
  const outside = await makeWorkspace();
  const root = await makeWorkspace();
  try {
    await writeFile(join(outside, "intake.bpmn"), "attacker-content");
    const nested = join(root, "nested");
    const parked = join(root, "parked");
    await mkdir(nested);
    await writeFile(join(nested, "intake.bpmn"), "legit-content");

    let swapping = true;
    const swapLoop = (async () => {
      while (swapping) {
        // Move the completed file rather than racing the reader against a partially written file.
        await rename(nested, parked);
        try {
          await symlink(outside, nested);
        } finally {
          await rm(nested, { force: true });
          await rename(parked, nested);
        }
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
          `a rejected read during a symlink race must fail with an explicit ProcessLandscapeError (${(result.reason as NodeJS.ErrnoException)?.code ?? "no errno"})`,
        );
      }
    }
  } finally {
    await rm(root, { recursive: true, force: true });
    await rm(outside, { recursive: true, force: true });
  }
});

test("Given an already-swapped symlinked directory component, readManifestFile rejects without reading its target", async () => {
  const outside = await makeWorkspace();
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
    const outside = await makeWorkspace();
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
