#!/usr/bin/env python3
"""Select and validate the bounded Markdown-only CI profile."""

import re
import subprocess
import sys
from pathlib import PurePosixPath
from urllib.parse import unquote, urlsplit


def git(*args):
    return subprocess.check_output(["git", *args], stderr=subprocess.PIPE)


def markdown(path):
    return path in ("README.md", "AGENTS.md", "CLAUDE.md",
                    ".github/copilot-instructions.md") or (
        path.startswith("docs/") and not path.startswith("docs/adr/")
        and path.endswith(".md"))


def changes(base, head):
    git("merge-base", "--is-ancestor", base, head)
    records = git("diff", "--raw", "-z", "--no-renames", base, head, "--").split(b"\0")
    result = []
    for index in range(0, len(records) - 1, 2):
        metadata = records[index].decode("ascii").split()
        path = records[index + 1].decode("utf-8")
        result.append((metadata[0][1:], metadata[1], metadata[4], path))
    return result


def mode(base, head):
    entries = changes(base, head)
    if entries and all(
            old in ("000000", "100644") and new == "100644"
            and status in ("A", "M") and markdown(path)
            for old, new, status, path in entries):
        return "docs"
    return "full"


def validate(base, head):
    if mode(base, head) != "docs":
        raise ValueError("source is not an approved Markdown-only change")
    git("diff", "--check", base, head, "--")
    checked = 0
    for _, _, _, path in changes(base, head):
        text = git("show", f"{head}:{path}").decode("utf-8")
        if "\0" in text:
            raise ValueError(f"{path}: Markdown contains NUL bytes")
        text = re.sub(r"(?ms)^(`{3,}|~{3,}).*?^\1[^\n]*$", "", text)
        text = re.sub(r"`[^`\n]+`", "", text)
        targets = re.findall(r"!?\[[^\]\n]*\]\(\s*(<[^>\n]+>|[^\s)]+)", text)
        targets += re.findall(r"(?m)^\s{0,3}\[[^\]\n]+\]:\s*(<[^>\n]+>|\S+)", text)
        for target in targets:
            target = target.strip("<>")
            url = urlsplit(target)
            if url.scheme or url.netloc or not url.path:
                continue
            if url.path.startswith("/"):
                raise ValueError(f"{path}: root-relative link requires manual review: {target}")
            parts = list(PurePosixPath(path).parent.parts)
            for part in PurePosixPath(unquote(url.path)).parts:
                if part == "..":
                    if not parts:
                        raise ValueError(f"{path}: link leaves the repository: {target}")
                    parts.pop()
                elif part != ".":
                    parts.append(part)
            destination = "/".join(parts)
            try:
                git("cat-file", "-e", f"{head}:{destination}")
            except subprocess.CalledProcessError as error:
                raise ValueError(f"{path}: missing local link: {target}") from error
            checked += 1
    print(f"docs-check: Markdown diff and {checked} local link targets passed; "
          "anchors, remote URLs and rendered diagrams are not validated")


def main():
    if len(sys.argv) != 4 or sys.argv[1] not in ("mode", "check"):
        raise SystemExit("usage: docs-check.py mode|check base head")
    try:
        if sys.argv[1] == "mode":
            print(mode(sys.argv[2], sys.argv[3]))
        else:
            validate(sys.argv[2], sys.argv[3])
    except (subprocess.CalledProcessError, ValueError, UnicodeError) as error:
        print(f"docs-check: {error}", file=sys.stderr)
        raise SystemExit(1) from error


if __name__ == "__main__":
    main()
