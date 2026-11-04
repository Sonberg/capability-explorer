#!/usr/bin/env python3
"""Check this repository's local OKF wiki profile, not semantic truth.

Uses the host's Ruby/Psych safe YAML parser; no product dependencies are needed.
Run from any directory. --self-test exercises positive and rejection fixtures.
"""

import argparse
from datetime import date, datetime
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from urllib.parse import unquote, urlsplit


ROOT = Path(__file__).resolve().parents[1]
LINK = re.compile(r"!?\[[^\]\n]*\]\(\s*(<[^>]+>|[^\s)]+)(?:\s+\"[^\"]*\")?\s*\)")
DEFINITION = re.compile(r"^\[\^([^\]]+)\]:\s*(\S.*)$", re.MULTILINE)
FOOTNOTE = re.compile(r"\[\^([^\]]+)\]")
YAML_PARSER = r"""
require "json"
require "yaml"
def unique_keys(node)
  return unless node.respond_to?(:children) && node.children
  if node.is_a?(Psych::Nodes::Mapping)
    keys = node.children.each_slice(2).map do |k, v|
      raise "non-scalar YAML key" unless k.is_a?(Psych::Nodes::Scalar)
      k.value
    end
    raise "duplicate YAML key" unless keys.uniq.length == keys.length
  end
  node.children.each { |child| unique_keys(child) }
end
result = JSON.parse(STDIN.read).map do |text|
  begin
    tree = Psych.parse_stream(text)
    raise "exactly one YAML document required" unless tree.children.length == 1
    unique_keys(tree)
    value = Psych.safe_load(text, permitted_classes: [], permitted_symbols: [], aliases: false)
    {"value" => value}
  rescue Psych::Exception, RuntimeError => e
    {"error" => e.message}
  end
end
puts JSON.generate(result)
"""


def without_code(text):
    """Mask fenced and inline code before inspecting authored Markdown links."""
    lines, fence = [], None
    for line in text.splitlines():
        marker = re.match(r"^\s*(`{3,}|~{3,})", line)
        if marker:
            token = marker.group(1)
            if fence is None:
                fence = token
            elif token[0] == fence[0] and len(token) >= len(fence):
                fence = None
            lines.append("")
        else:
            lines.append("" if fence else line)
    return re.sub(r"`[^`\n]+`", "", "\n".join(lines))


def check(repo, wiki):
    errors = []
    paths = sorted(wiki.rglob("*.md"))
    if not paths:
        return ["wiki contains no Markdown pages"]
    frontmatter, bodies = {}, {}

    def fail(path, message):
        errors.append(f"{path.relative_to(repo)}: {message}")

    yaml_paths, yaml_texts = [], []
    for path in paths:
        text = path.read_text(encoding="utf-8")
        if text.startswith("---\n"):
            end = text.find("\n---\n", 4)
            if end < 0:
                fail(path, "unterminated YAML frontmatter")
                bodies[path] = ""
                continue
            yaml_paths.append(path)
            yaml_texts.append(text[4:end])
            bodies[path] = text[end + 5:]
        else:
            bodies[path] = text

    result = subprocess.run(
        ["ruby", "-e", YAML_PARSER], input=json.dumps(yaml_texts),
        text=True, capture_output=True, check=True,
    )
    parsed_documents = json.loads(result.stdout)
    if len(parsed_documents) != len(yaml_paths):
        raise ValueError("YAML parser returned an unexpected document count")
    for path, parsed in zip(yaml_paths, parsed_documents):
        if "error" in parsed:
            fail(path, f"invalid YAML: {parsed['error']}")
        elif not isinstance(parsed["value"], dict):
            fail(path, "frontmatter must be a mapping")
        else:
            frontmatter[path] = parsed["value"]

    def resolve(path, target):
        parsed = urlsplit(target)
        if parsed.scheme:
            if parsed.scheme not in {"https", "http", "mailto"}:
                fail(path, f"unsupported resource scheme: {target}")
            elif parsed.scheme != "mailto" and not parsed.netloc:
                fail(path, f"invalid external resource: {target}")
            return None
        if parsed.netloc:
            fail(path, f"protocol-relative resource not allowed: {target}")
            return None
        raw = unquote(parsed.path)
        if "\\" in raw or "\x00" in raw:
            fail(path, f"invalid local resource: {target}")
            return None
        destination = (
            wiki / raw.lstrip("/") if raw.startswith("/")
            else path.parent / raw if raw else path
        ).resolve()
        if not destination.is_relative_to(repo):
            fail(path, f"resource escapes repository: {target}")
        elif not destination.exists():
            fail(path, f"missing local resource: {target}")
        else:
            return destination
        return None

    links = {}
    concepts = set()
    for path in paths:
        meta = frontmatter.get(path, {})
        body = without_code(bodies[path])
        links[path] = set()
        for match in LINK.finditer(body):
            target = resolve(path, match.group(1).strip("<>"))
            if target:
                links[path].add(target)
        if path.name == "index.md":
            expected = {"okf_version": "0.2"} if path == wiki / "index.md" else {}
            if meta != expected or (path != wiki / "index.md" and path in yaml_paths):
                fail(path, "reserved index frontmatter violates local OKF profile")
            continue
        if path.name == "log.md":
            if path in yaml_paths:
                fail(path, "reserved log must not have frontmatter")
            dates = re.findall(r"^## (\d{4}-\d{2}-\d{2})$", body, re.MULTILINE)
            try:
                valid_dates = [date.fromisoformat(value) for value in dates]
            except ValueError:
                valid_dates = []
            if not valid_dates or len(valid_dates) != len(dates) or dates != sorted(dates, reverse=True):
                fail(path, "log requires valid newest-first ISO date headings")
            continue

        concepts.add(path)
        for key in ("type", "title", "description"):
            if not isinstance(meta.get(key), str) or not meta[key].strip():
                fail(path, f"missing/non-string {key}")
        if meta.get("status") not in {"draft", "stable", "deprecated"}:
            fail(path, "invalid lifecycle status")
        tags = meta.get("tags")
        if not isinstance(tags, list) or not tags or any(
            not isinstance(tag, str) or not tag.strip() for tag in tags
        ):
            fail(path, "tags must be a nonempty string list")
        generated = meta.get("generated")
        if not isinstance(generated, dict) or not isinstance(generated.get("by"), str) or not generated["by"].strip():
            fail(path, "generated.by is required")
        else:
            try:
                stamp = datetime.fromisoformat(generated.get("at", "").replace("Z", "+00:00"))
                if stamp.tzinfo is None:
                    raise ValueError("timezone required")
            except (ValueError, TypeError, AttributeError):
                fail(path, "generated.at must be an ISO timestamp with timezone")
        if "verified" in meta:
            fail(path, "verified attestations are not supported by this local draft profile")

        sources = meta.get("sources")
        ids = []
        if not isinstance(sources, list) or not sources:
            fail(path, "sources must be a nonempty list")
            sources = []
        for source in sources:
            if not isinstance(source, dict):
                fail(path, "source must be a mapping")
                continue
            sid, resource = source.get("id"), source.get("resource")
            if not isinstance(sid, str) or not re.fullmatch(r"[A-Za-z0-9_-]+", sid):
                fail(path, "source id must be a nonempty citation key")
            else:
                ids.append(sid)
            if not isinstance(resource, str) or not resource.strip():
                fail(path, "source resource is required")
            else:
                resolve(path, resource)
        if len(set(ids)) != len(ids):
            fail(path, "duplicate source id")
        definitions = DEFINITION.findall(body)
        labels = [label for label, _ in definitions]
        uses = set(FOOTNOTE.findall(DEFINITION.sub("", body)))
        if len(set(labels)) != len(labels):
            fail(path, "duplicate footnote definition")
        if uses != set(labels) or uses != set(ids):
            fail(path, "source ids, footnote uses and definitions must match")

    for required in ("index.md", "log.md"):
        if wiki / required not in bodies:
            errors.append(f"docs/wiki/{required}: required reserved page missing")
    for directory in sorted({p.parent for p in paths}):
        index = directory / "index.md"
        if index not in bodies:
            errors.append(f"{directory.relative_to(repo)}: directory index missing")
            continue
        children = {p for p in paths if p.parent == directory and p != index}
        subindexes = {p for p in paths if p.name == "index.md" and p.parent.parent == directory}
        for target in sorted(children | subindexes):
            if target not in links.get(index, set()):
                fail(index, f"index omits {target.relative_to(directory)}")
    connected = set()
    for origin in concepts:
        for target in links.get(origin, set()) & concepts:
            if target != origin:
                connected.update((origin, target))
    for path in sorted(concepts - connected):
        fail(path, "orphan concept: add a meaningful non-index relationship")
    return errors


def self_test():
    meta = """---
type: Concept
title: Example
description: A test concept.
status: draft
tags: [test]
generated: {by: test, at: "2026-11-04T00:00:00Z"}
sources:
  - id: src
    resource: ../evidence.txt
---
Claim.[^src]

[Other](other.md)

[^src]: Exact source passage.
"""
    cases = {
        "valid": (None, None),
        "bad-yaml": ("page", meta.replace("type: Concept", "type: [")),
        "duplicate-yaml": ("page", meta.replace("type: Concept", "type: Concept\ntype: Other")),
        "unsafe-yaml": ("page", meta.replace("type: Concept", "type: !ruby/object:Object {}")),
        "missing-type": ("page", meta.replace("type: Concept\n", "")),
        "missing-resource": ("page", meta.replace("resource: ../evidence.txt", "note: missing")),
        "unmatched-citation": ("page", meta.replace("Claim.[^src]", "Claim.[^missing]")),
        "duplicate-citation": ("page", meta + "\n[^src]: Duplicate.\n"),
        "missing-link": ("page", meta.replace("other.md", "missing.md")),
        "escape": ("page", meta.replace("../evidence.txt", "../../outside.txt")),
        "bad-status": ("page", meta.replace("status: draft", "status: approved")),
        "false-verified": ("page", meta.replace("status: draft", "status: draft\nverified: true")),
        "bad-generated": ("page", meta.replace("2026-11-04T00:00:00Z", "yesterday")),
        "orphan": ("both", meta.replace("[Other](other.md)", "")),
        "index-omission": ("index", '---\nokf_version: "0.2"\n---\n[Log](log.md)\n'),
        "bad-log": ("log", "## 2026-02-31\nImpossible date\n"),
        "bad-root-version": ("index", '---\nokf_version: "9"\n---\n[Page](page.md)\n[Other](other.md)\n[Log](log.md)\n'),
    }
    failures = []
    for name, (target, replacement) in cases.items():
        with tempfile.TemporaryDirectory(prefix="okf-wiki-test-") as directory:
            repo = Path(directory).resolve()
            wiki = repo / "wiki"
            wiki.mkdir()
            (repo / "evidence.txt").write_text("Source.\n")
            pages = {
                "page.md": meta,
                "other.md": meta.replace("other.md", "page.md"),
                "index.md": '---\nokf_version: "0.2"\n---\n[Page](page.md)\n[Other](other.md)\n[Log](log.md)\n',
                "log.md": "## 2026-11-04\nCreated.\n",
            }
            if target == "both":
                pages["page.md"] = replacement
                pages["other.md"] = replacement
            elif target:
                pages[f"{target}.md"] = replacement
            for filename, text in pages.items():
                (wiki / filename).write_text(text, encoding="utf-8")
            errors = check(repo, wiki)
            if (name == "valid" and errors) or (name != "valid" and not errors):
                failures.append(f"{name}: unexpected result {errors}")
    if failures:
        raise RuntimeError("\n".join(failures))
    print(f"PASS: {len(cases)} positive/rejection fixtures")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    try:
        if args.self_test:
            self_test()
            return 0
        wiki = ROOT / "docs/wiki"
        errors = check(ROOT, wiki)
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print(f"ERROR: wiki validation unavailable: {error}", file=sys.stderr)
        return 1
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    count = len(list(wiki.rglob("*.md")))
    print(f"PASS: {count} Markdown pages; YAML, metadata, source/citation joins, local paths, indexes and relationships")
    print("Not checked: semantic truth, external URL availability, owner approval or runtime behavior.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
