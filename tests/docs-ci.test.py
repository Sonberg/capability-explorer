import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("docs_check", ROOT / "scripts/ci/docs-check.py")
docs = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(docs)


class DocsCiTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.git_patch = patch.object(docs, "git", side_effect=self.git)
        self.git_patch.start()
        self.addCleanup(self.git_patch.stop)
        self.git("init", "-q", "-b", "main")
        self.git("config", "user.name", "Docs CI test")
        self.git("config", "user.email", "docs-ci@example.invalid")
        self.git("config", "commit.gpgsign", "false")
        self.write("README.md", "# Example\n")
        self.write("src/code.ts", "export const value = 1;\n")
        self.base = self.commit()

    def git(self, *args):
        return subprocess.check_output(["git", "-C", str(self.root), *args],
                                       stderr=subprocess.PIPE)

    def write(self, path, text):
        destination = self.root / path
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(text)

    def commit(self):
        self.git("add", ".")
        self.git("commit", "-qm", "fixture")
        return self.git("rev-parse", "HEAD").decode().strip()

    def test_markdown_only_addition_validates_links_without_build_tools(self):
        self.write("docs/example.md", "# Example\n\n[Source](../src/code.ts)\n[Home](../README.md)\n")
        head = self.commit()
        self.assertEqual("docs", docs.mode(self.base, head))
        docs.validate(self.base, head)

    def test_every_unapproved_path_and_mixed_diff_requires_full_ci(self):
        for path in ("src/readme.md", "docs/example.py", ".github/workflows/ci.yml",
                     ".github/ISSUE_TEMPLATE/bug.yml", "package.json",
                     "scripts/ci/docs-check.py", "tests/docs-ci.test.py", "docs/adr/example.md"):
            with self.subTest(path=path):
                base = self.git("rev-parse", "HEAD").decode().strip()
                self.write("docs/approved.md", f"# {path}\n")
                self.write(path, "changed\n")
                self.assertEqual("full", docs.mode(base, self.commit()))

    def test_deletions_moves_from_code_and_executable_markdown_require_full_ci(self):
        (self.root / "README.md").unlink()
        head = self.commit()
        self.assertEqual("full", docs.mode(self.base, head))
        self.git("mv", "src/code.ts", "docs-code.md")
        moved = self.commit()
        self.assertEqual("full", docs.mode(head, moved))
        self.write("docs/executable.md", "# Executable\n")
        os.chmod(self.root / "docs/executable.md", 0o755)
        self.assertEqual("full", docs.mode(moved, self.commit()))

    def test_symlink_markdown_and_empty_diff_require_full_ci(self):
        self.assertEqual("full", docs.mode(self.base, self.base))
        (self.root / "AGENTS.md").symlink_to("src/code.ts")
        self.assertEqual("full", docs.mode(self.base, self.commit()))

    def test_missing_local_link_and_traversal_fail_documentation_gate(self):
        for target in ("missing.md", "../../outside.md"):
            with self.subTest(target=target):
                self.write("docs/example.md", f"# Example\n\n[Link]({target})\n")
                with self.assertRaisesRegex(ValueError, "missing local link|leaves the repository"):
                    docs.validate(self.base, self.commit())

    def test_invalid_revision_cannot_select_lightweight_success(self):
        with self.assertRaises(subprocess.CalledProcessError):
            docs.mode("missing-revision", self.base)


if __name__ == "__main__":
    unittest.main()
