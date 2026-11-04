---
type: Playbook
title: Maintaining a public OKF knowledge wiki
description: Source attribution, connected knowledge, lifecycle and publication controls.
status: draft
tags: [wiki, provenance, publication]
generated: {by: copilot-cli/1.0.105, at: "2026-11-04T01:10:00+01:00"}
sources:
  - id: okf
    resource: https://github.com/GoogleCloudPlatform/open-knowledge-format/blob/main/SPEC.md
  - id: pattern
    resource: https://gist.github.com/karpathy/442a6bf555914893e9891c11519de94f
---
# Knowledge, sources and maintenance

Use persistent topic pages, source summaries and cross-links rather than a new
disconnected report for each run. Keep source custody separate from synthesized
knowledge. This follows the LLM Wiki pattern; the exact taxonomy is local.[^pattern]

The local OKF profile uses UTF-8 Markdown, YAML `type`, source resources and
source-keyed footnotes. It additionally requires title, description, tags,
lifecycle status and timestamped generation metadata. These stronger requirements
are repository conventions, not universal OKF requirements.[^okf]

## Ingest and query

Identify the exact source before making a claim. Separate source statements,
observed code, interpretation, proposals and accepted decisions. A retrieval
timestamp is not a source update timestamp; a generated timestamp does not make
a claim fresh. Preserve uncertainty, contradictions, failed gates and source
limits. Start queries at the index and follow evidence before answering.

Update relevant topic pages, source catalogue, relationships, indexes and log in
one change. Keep diagrams of implemented behavior in
[architecture](../architecture/index.md), not duplicated across topics.
The [collector contract](collector-contract.md) applies the same provenance
discipline to product exports.

## Publication boundary

This public wiki must not contain private transcripts, dumps, customer details,
internal source payloads, local personal paths or confidential donor code.
Keep restricted evidence in an appropriately authorized private store. Public
summaries must be independently safe to publish; hiding files in a later commit
does not remove them from Git history.

Source permission, reuse permission and public redistribution permission are
separate decisions. A contributor's ownership assertion does not remove
third-party notice obligations. Do not copy donor code until exact assets and
applicable terms are reviewed.

## Lint and review

Only the root index has `okf_version: "0.2"` frontmatter. Other indexes and
`log.md` have none. Log headings use newest-first ISO dates. Concept metadata
source IDs must match cited footnotes; links should connect meaningful topics,
not only indexes. Use `draft` until relevant review.

```sh
python3 scripts/check-wiki.py --self-test
python3 scripts/check-wiki.py
```

The checker validates metadata, citation joins, repository-contained paths,
indexes and relationships. It does not validate semantic truth, external URLs,
publication clearance or runtime behavior. Never infer `verified` from lint.

[^okf]: Retained OKF 0.2 reading: concepts, provenance, trust, paths, indexes and logs; not fetched anew in the bounded qualification round.
[^pattern]: Retained LLM Wiki reading: persistent knowledge layer, sources, ingest/query/lint and indexing; not fetched anew in this round.
