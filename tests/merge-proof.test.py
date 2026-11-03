import copy
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("merge_proof", ROOT / "scripts/ci/merge-proof.py")
proof = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(proof)


class MergeProofTests(unittest.TestCase):
    def setUp(self):
        self.context = {
            "repository": "owner/repo", "before": "b" * 40, "after": "a" * 40,
            "tree": "t" * 40, "workflow": "w" * 40, "trusted_workflow": "w" * 40,
        }
        self.protection = {
            "required_status_checks": {"strict": True, "contexts": ["ci"]},
            "enforce_admins": {"enabled": True},
            "required_pull_request_reviews": {
                "bypass_pull_request_allowances": {"users": [], "teams": [], "apps": []}
            },
        }
        self.pull = {
            "number": 7, "merged_at": "2026-11-03T00:00:00Z",
            "merge_commit_sha": "a" * 40,
            "base": {"ref": "main", "repo": {"full_name": "owner/repo"}},
            "head": {"sha": "h" * 40, "repo": {"full_name": "owner/repo"}},
        }
        self.run = {
            "id": 12, "run_attempt": 1, "event": "pull_request",
            "conclusion": "success", "status": "completed",
            "path": ".github/workflows/ci.yml", "head_sha": "h" * 40,
            "updated_at": "2026-11-02T23:59:00Z",
            "repository": {"full_name": "owner/repo"},
        }
        self.receipt = {
            "version": 1, "repository": "owner/repo", "run_id": 12,
            "run_attempt": 1, "pr": 7, "base": "b" * 40, "head": "h" * 40,
            "tree": "t" * 40, "workflow": "w" * 40, "protected": True,
            "tested_sha": "m" * 40,
        }

    def verify(self):
        return proof.verify_evidence(
            self.context, self.protection, self.pull, self.run, self.receipt)

    def test_given_successful_exact_merge_when_verified_then_lightweight(self):
        self.assertTrue(self.verify())

    def test_given_title_or_body_edit_then_metadata_only(self):
        for field in ("title", "body"):
            self.assertTrue(proof.metadata_only(
                "pull_request", {"action": "edited", "changes": {field: {"from": "old"}}}))

    def test_given_retarget_or_unknown_edit_then_full_gate(self):
        for event in (
            {"action": "edited", "changes": {"base": {"ref": {"from": "stage"}}}},
            {"action": "edited", "changes": {}},
            {"action": "synchronize"},
            {"action": "opened"},
        ):
            self.assertFalse(proof.metadata_only("pull_request", event))
        self.assertFalse(proof.metadata_only("push", {"action": "edited"}))

    def test_given_any_proof_mismatch_then_full_gate(self):
        changes = [
            ("context", "tree", "changed"),
            ("context", "before", "new-base"),
            ("context", "trusted_workflow", "changed"),
            ("run", "conclusion", "failure"),
            ("run", "status", "in_progress"),
            ("run", "event", "push"),
            ("run", "path", "other.yml"),
            ("run", "head_sha", "old-head"),
            ("run", "run_attempt", 2),
            ("run", "updated_at", "2026-11-04T00:00:00Z"),
            ("receipt", "protected", False),
            ("receipt", "version", 2),
            ("receipt", "pr", 8),
            ("receipt", "workflow", "changed"),
            ("receipt", "repository", "another/repo"),
            ("pull", "merge_commit_sha", "other"),
            ("pull", "merged_at", None),
        ]
        originals = {name: copy.deepcopy(getattr(self, name))
                     for name in ("context", "run", "receipt", "pull")}
        for name, field, value in changes:
            with self.subTest(name=name, field=field):
                for key, original in originals.items():
                    setattr(self, key, copy.deepcopy(original))
                getattr(self, name)[field] = value
                self.assertFalse(self.verify())

    def test_given_unknown_or_weak_protection_then_full_gate(self):
        for protection in (
            {}, {"message": "Forbidden"},
            {**self.protection, "enforce_admins": {"enabled": False}},
            {**self.protection, "required_status_checks": {"strict": False, "contexts": ["ci"]}},
            {**self.protection, "required_status_checks": {"strict": True, "contexts": ["other"]}},
            {**self.protection, "required_pull_request_reviews": {
                "bypass_pull_request_allowances": {"users": [{"login": "admin"}]}}},
        ):
            with self.subTest(protection=protection):
                self.assertFalse(proof.verify_evidence(
                    self.context, protection, self.pull, self.run, self.receipt))

    def test_given_fork_or_other_target_then_full_gate(self):
        self.pull["head"]["repo"]["full_name"] = "fork/repo"
        self.assertFalse(self.verify())
        self.pull["head"]["repo"]["full_name"] = "owner/repo"
        self.pull["base"]["ref"] = "stage"
        self.assertFalse(self.verify())

    def test_given_local_act_or_metadata_then_no_remote_evidence_needed(self):
        with patch.dict(os.environ, {"ACT": "true"}, clear=True), patch.object(
                proof, "api", side_effect=AssertionError("unexpected network")):
            self.assertEqual("full", proof.select({}, "owner/repo")[0])
        with patch.dict(os.environ, {"GITHUB_EVENT_NAME": "pull_request"}, clear=True):
            mode = proof.select({"action": "edited", "changes": {"body": {}}}, "owner/repo")[0]
            self.assertEqual("metadata", mode)

    def test_given_protection_api_denied_then_cli_explicitly_falls_back_to_full(self):
        with tempfile.TemporaryDirectory() as directory:
            event = Path(directory) / "event.json"
            output = Path(directory) / "output"
            event.write_text(json.dumps({
                "ref": "refs/heads/main", "before": "b" * 40, "after": "a" * 40}))
            env = {"GITHUB_EVENT_NAME": "push", "GITHUB_EVENT_PATH": str(event),
                   "GITHUB_REPOSITORY": "owner/repo", "GITHUB_OUTPUT": str(output)}
            with patch.dict(os.environ, env, clear=True), patch.object(
                    proof, "git", return_value="a" * 40), patch.object(
                    proof, "api", side_effect=proof.EvidenceUnavailable("HTTP403")), patch(
                    "sys.argv", ["merge-proof.py", "select"]):
                proof.main()
            self.assertEqual("mode=full\n", output.read_text())

    def test_given_bad_or_extra_archive_entries_then_evidence_rejected(self):
        for entries in ({"../merge-proof.json": "{}"},
                        {"merge-proof.json": "[]"},
                        {"merge-proof.json": "not-json"},
                        {"merge-proof.json": "{}", "unexpected": "x"}):
            data = io.BytesIO()
            with zipfile.ZipFile(data, "w") as archive:
                for name, value in entries.items():
                    archive.writestr(name, value)
            artifacts = {"artifacts": [{
                "name": "ci-proof-12-1", "id": 4,
                "expired": False, "size_in_bytes": len(data.getvalue())}]}
            with patch.object(proof, "api", side_effect=[artifacts, data.getvalue()]):
                with self.assertRaises(proof.EvidenceUnavailable):
                    proof.artifact_receipt("owner/repo", self.run)

    def test_given_matched_receipt_then_verify_actual_tested_commit_tree_and_parents(self):
        commit = {"tree": {"sha": "t" * 40},
                  "parents": [{"sha": "b" * 40}, {"sha": "h" * 40}]}
        self.assertTrue(proof.tested_commit_matches(self.receipt, commit))
        commit["parents"][0]["sha"] = "other-base"
        self.assertFalse(proof.tested_commit_matches(self.receipt, commit))

    def test_given_complete_github_evidence_then_select_lightweight_only_for_exact_commit(self):
        event = {"ref": "refs/heads/main", "before": "b" * 40, "after": "a" * 40}
        commit = {"tree": {"sha": "t" * 40},
                  "parents": [{"sha": "b" * 40}, {"sha": "h" * 40}]}
        def git(expression):
            return {"HEAD": "a" * 40, "HEAD^{tree}": "t" * 40,
                    "HEAD:.github/workflows/ci.yml": "w" * 40,
                    f"{'b' * 40}:.github/workflows/ci.yml": "w" * 40}[expression]
        def api(repo, path):
            return {
                "branches/main/protection": self.protection,
                f"commits/{'a' * 40}/pulls?per_page=100": [{"number": 7}],
                "pulls/7": self.pull,
                f"actions/workflows/ci.yml/runs?event=pull_request&head_sha={'h' * 40}&status=success&per_page=5":
                    {"workflow_runs": [self.run]},
                f"git/commits/{'m' * 40}": commit,
            }[path]
        with patch.dict(os.environ, {"GITHUB_EVENT_NAME": "push"}, clear=True), patch.object(
                proof, "git", side_effect=git), patch.object(proof, "api", side_effect=api), patch.object(
                proof, "artifact_receipt", return_value=self.receipt):
            self.assertEqual("verified", proof.select(event, "owner/repo")[0])
            commit["tree"]["sha"] = "other-tree"
            self.assertEqual("full", proof.select(event, "owner/repo")[0])
            commit["tree"]["sha"] = "t" * 40
            with patch.object(proof, "artifact_receipt", side_effect=proof.EvidenceUnavailable("expired")):
                self.assertEqual("full", proof.select(event, "owner/repo")[0])


if __name__ == "__main__":
    unittest.main()
