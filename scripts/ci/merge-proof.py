#!/usr/bin/env python3
"""Verify exact-tree pre-merge evidence before reducing post-merge CI."""

import io
from datetime import datetime
import json
import os
from pathlib import Path
import subprocess
import sys
import zipfile


WORKFLOW = ".github/workflows/ci.yml"


class EvidenceUnavailable(Exception):
    pass


def metadata_only(event_name, event):
    changes = event.get("changes", {})
    return (event_name == "pull_request" and event.get("action") == "edited"
            and bool(changes) and set(changes) <= {"title", "body"})


def protected(protection):
    checks = protection.get("required_status_checks") or {}
    contexts = set(checks.get("contexts", []))
    contexts.update(c.get("context") for c in checks.get("checks", []))
    reviews = protection.get("required_pull_request_reviews") or {}
    bypass = reviews.get("bypass_pull_request_allowances") or {}
    return (checks.get("strict") is True and "ci" in contexts
            and (protection.get("enforce_admins") or {}).get("enabled") is True
            and bool(reviews)
            and not any(bypass.get(kind) for kind in ("users", "teams", "apps")))


def verify_evidence(context, protection, pull, run, receipt):
    repo = context["repository"]
    base = pull.get("base") or {}
    head = pull.get("head") or {}
    try:
        completed = datetime.fromisoformat(run.get("updated_at", "").replace("Z", "+00:00"))
        merged = datetime.fromisoformat((pull.get("merged_at") or "").replace("Z", "+00:00"))
        before_merge = completed.tzinfo is not None and merged.tzinfo is not None and completed <= merged
    except ValueError:
        before_merge = False
    return all((
        before_merge,
        protected(protection),
        bool(pull.get("merged_at")),
        pull.get("merge_commit_sha") == context["after"],
        base.get("ref") == "main",
        (base.get("repo") or {}).get("full_name") == repo,
        (head.get("repo") or {}).get("full_name") == repo,
        context["workflow"] == context["trusted_workflow"],
        run.get("event") == "pull_request",
        run.get("status") == "completed",
        run.get("conclusion") == "success",
        run.get("path") == WORKFLOW,
        (run.get("repository") or {}).get("full_name") == repo,
        run.get("head_sha") == head.get("sha"),
        receipt.get("version") == 1,
        receipt.get("protected") is True,
        receipt.get("repository") == repo,
        receipt.get("pr") == pull.get("number"),
        receipt.get("run_id") == run.get("id"),
        receipt.get("run_attempt") == run.get("run_attempt"),
        receipt.get("base") == context["before"],
        receipt.get("head") == head.get("sha"),
        receipt.get("tree") == context["tree"],
        receipt.get("workflow") == context["workflow"],
        bool(receipt.get("tested_sha")),
    ))


def tested_commit_matches(receipt, commit):
    return ((commit.get("tree") or {}).get("sha") == receipt.get("tree")
            and [parent.get("sha") for parent in commit.get("parents", [])]
            == [receipt.get("base"), receipt.get("head")])


def command(*args):
    return subprocess.check_output(args, stderr=subprocess.PIPE)


def git(expression):
    return command("git", "rev-parse", expression).decode().strip()


def api(repo, path, binary=False):
    try:
        data = command("gh", "api", f"repos/{repo}/{path}")
    except subprocess.CalledProcessError as error:
        detail = error.stderr.decode(errors="replace").strip()
        raise EvidenceUnavailable(f"GitHub evidence API failed: {detail}") from error
    if binary:
        return data
    try:
        return json.loads(data)
    except json.JSONDecodeError as error:
        raise EvidenceUnavailable("GitHub evidence API returned invalid JSON") from error


def artifact_receipt(repo, run):
    artifacts = api(repo, f"actions/runs/{run['id']}/artifacts?per_page=100")
    name = f"ci-proof-{run['id']}-{run['run_attempt']}"
    matches = [a for a in artifacts.get("artifacts", [])
               if a.get("name") == name and not a.get("expired")]
    if len(matches) != 1 or not 0 < matches[0].get("size_in_bytes", 0) <= 65536:
        raise EvidenceUnavailable("missing, expired, ambiguous or oversized proof artifact")
    data = api(repo, f"actions/artifacts/{matches[0]['id']}/zip", binary=True)
    if len(data) > 65536:
        raise EvidenceUnavailable("oversized proof archive")
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            if archive.namelist() != ["merge-proof.json"]:
                raise EvidenceUnavailable("unexpected proof archive entries")
            entry = archive.getinfo("merge-proof.json")
            if not 0 < entry.file_size <= 16384:
                raise EvidenceUnavailable("oversized proof receipt")
            receipt = json.loads(archive.read(entry))
            if not isinstance(receipt, dict):
                raise EvidenceUnavailable("proof receipt must be an object")
            return receipt
    except (zipfile.BadZipFile, json.JSONDecodeError, UnicodeDecodeError) as error:
        raise EvidenceUnavailable("invalid proof artifact") from error


def select(event, repo):
    if os.environ.get("ACT") == "true":
        return "full", "local act always executes the complete canonical gate"
    if metadata_only(os.environ.get("GITHUB_EVENT_NAME"), event):
        return "metadata", "title/body-only edit; preserve previous full-ci check"
    if os.environ.get("GITHUB_EVENT_NAME") == "pull_request":
        pull = event["pull_request"]
        base, head = pull["base"]["sha"], pull["head"]["sha"]
        profile = command("python3", "scripts/ci/docs-check.py", "mode", base, head).decode().strip()
        if profile == "docs":
            return "docs", "only approved regular Markdown additions/edits"
    if os.environ.get("GITHUB_EVENT_NAME") != "push":
        return "full", "pre-merge source/base change requires full CI"
    if event.get("ref") != "refs/heads/main":
        return "full", "not a main push"
    before, after = event.get("before", ""), event.get("after", "")
    if not before or before == "0" * 40 or after != git("HEAD"):
        return "full", "no exact single-push source identity"
    profile = command("python3", "scripts/ci/docs-check.py", "mode", before, after).decode().strip()
    if profile == "docs":
        return "docs", "main push contains only approved Markdown additions/edits"
    protection = api(repo, "branches/main/protection")
    if not protected(protection):
        return "full", "strict required ci/admin enforcement is not proven"
    context = {
        "repository": repo, "before": before, "after": after,
        "tree": git("HEAD^{tree}"), "workflow": git(f"HEAD:{WORKFLOW}"),
        "trusted_workflow": git(f"{before}:{WORKFLOW}"),
    }
    pulls = api(repo, f"commits/{after}/pulls?per_page=100")
    for candidate in pulls:
        pull = api(repo, f"pulls/{candidate['number']}")
        if not pull.get("merged_at") or pull.get("merge_commit_sha") != after:
            continue
        head = (pull.get("head") or {}).get("sha", "")
        runs = api(repo, f"actions/workflows/ci.yml/runs?event=pull_request&head_sha={head}&status=success&per_page=5")
        for run in runs.get("workflow_runs", []):
            try:
                receipt = artifact_receipt(repo, run)
            except EvidenceUnavailable as error:
                print(f"merge-proof: candidate rejected: {error}", file=sys.stderr)
                continue
            if verify_evidence(context, protection, pull, run, receipt):
                commit = api(repo, f"git/commits/{receipt['tested_sha']}")
                if tested_commit_matches(receipt, commit):
                    return "verified", f"exact merged tree passed PR #{pull['number']} run {run['id']}"
    return "full", "no successful protected pre-merge proof matches this push/tree/base"


def emit(event, repo, destination):
    pull = event["pull_request"]
    base, head = pull["base"]["sha"], pull["head"]["sha"]
    parents = command("git", "show", "-s", "--format=%P", "HEAD").decode().split()
    if parents != [base, head]:
        raise EvidenceUnavailable("tested checkout is not the event's exact base/head merge")
    try:
        enforced = protected(api(repo, "branches/main/protection"))
    except EvidenceUnavailable as error:
        print(f"merge-proof: receipt not reusable: {error}", file=sys.stderr)
        enforced = False
    receipt = {
        "version": 1, "repository": repo, "pr": pull["number"],
        "base": base, "head": head, "tree": git("HEAD^{tree}"),
        "tested_sha": git("HEAD"), "workflow": git(f"HEAD:{WORKFLOW}"),
        "run_id": int(os.environ["GITHUB_RUN_ID"]),
        "run_attempt": int(os.environ["GITHUB_RUN_ATTEMPT"]),
        "protected": enforced,
    }
    Path(destination).write_text(json.dumps(receipt, sort_keys=True) + "\n")


def aligned(event, repo):
    pull = event["pull_request"]
    base = pull["base"]["sha"]
    if api(repo, "git/ref/heads/main")["object"]["sha"] != base:
        raise EvidenceUnavailable("main advanced; update the PR and rerun full CI")
    head = pull["head"]["sha"]
    if command("git", "show", "-s", "--format=%P", "HEAD").decode().split() != [base, head]:
        raise EvidenceUnavailable("checkout is not the event base/head merge")
    subprocess.run(["git", "merge-base", "--is-ancestor", base, head], check=True)
    print(f"merge-proof: PR head {head} contains current main {base}")


def main():
    if len(sys.argv) not in (2, 3) or sys.argv[1] not in ("select", "emit", "aligned"):
        raise SystemExit("usage: merge-proof.py select | emit path | aligned")
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text())
    repo = os.environ["GITHUB_REPOSITORY"]
    if sys.argv[1] == "aligned":
        aligned(event, repo)
        return
    if sys.argv[1] == "emit":
        if len(sys.argv) != 3:
            raise SystemExit("emit requires destination")
        emit(event, repo, sys.argv[2])
        return
    try:
        mode, reason = select(event, repo)
    except EvidenceUnavailable as error:
        mode, reason = "full", str(error)
    except subprocess.CalledProcessError as error:
        mode, reason = "full", f"git identity lookup failed (exit {error.returncode})"
    print(f"merge-proof: {mode}: {reason}")
    with open(os.environ["GITHUB_OUTPUT"], "a") as output:
        output.write(f"mode={mode}\n")


if __name__ == "__main__":
    main()
