# Maintain a curated project changelog

* Status: accepted
* Date: 2026-11-01
* Decision owner: repository owner and contributors
* Related: [MADR-0005](0005-pr-lifecycle-and-conflict-prevention.md), [MADR-0007](0007-semantic-versioning.md)

## Approval and scope (2026-11-09)

Accepted by explicit user/operator approval: "I approve all MADRs that are in draft",
clarified as "All 37 proposed/pending MADRs on current main".
Accepted scope: decision policy/design for Maintain a curated project changelog; within the stated outcome and qualifications.
Approval: [issue #3, comment 6742261485](https://github.com/Sonberg/capability-explorer/issues/3#issuecomment-6742261485).
Reviewed main: `9767bbcb7249375574b2306596389f7ccaa3a2a2`; original record: `sha256:4fc0a57997ee2072da68b69aa103bf5cb29e42e8971333112e60c027259ab7d3`.
Previous decision status (historical): `applied to this operator's work; proposed for repository-wide adoption`.
Historical proposal wording below records the original rationale, not current status.
Acceptance approves the decision method/design only. It does not establish
implementation, measured SLO compliance, particular retention durations, real-source
access, business assertions, deletion authority or experimental-tool activation.
Existing operational exceptions and separately required action approvals remain in force.

## Context and problem

Changes currently appear across PRs, issues and handoffs. Contributors and users
need a durable account of notable behavior changes without treating commit
messages, merged PRs or private package versions as published releases.

## Decision drivers

Human readability, accurate release history, compatibility visibility, low CI
cost, and fewer conflicts between concurrent contributors.

## Considered options

1. Generate release notes directly from every commit.
2. Curate a single project changelog using Keep a Changelog 1.1.0.
3. Maintain independent informal notes in each component.

## Decision outcome

Choose option 2. [docs/CHANGELOG.md](../CHANGELOG.md) is authoritative. Its location
follows the repository rule that documentation belongs under `docs/`.
Use an Unreleased section, newest releases first, and dated release headings
(`YYYY-MM-DD`). Group notable entries under Added, Changed, Deprecated, Removed,
Fixed or Security, omitting empty groups. Describe user impact, migration needs
and relevant issue/PR links; do not dump commit logs.

Each PR either supplies its notable changelog entry or explains why none is
needed. The integration owner resolves duplicate entries and updates the shared
file after checking claims. Parallel workers hand proposed entries to that owner
instead of editing the same section simultaneously. A draft entry on a feature
branch describes that branch; it becomes part of stage only through review.

Only an explicitly authorized release moves relevant Unreleased entries into a
version/date section. Link released versions to their tags and comparisons to the
previous release once those refs actually exist. Preserve an Unreleased section.
Do not invent historical versions/dates or label an unmerged PR as released.
Mark a withdrawn release as yanked and explain it; do not erase its history.
Correct factual documentation errors transparently without changing released
artifact bytes.

## Consequences and verification

Human curation adds small review effort but avoids meaningless release noise.
The first changelog records adoption work only, not an invented project history.
No new dependency, image, hosted job or release automation is introduced.
Reviewers check entry relevance, categories, links and version/date consistency;
normal stage alignment and full local CI remain required by MADR-0003/0005.
This is a documented review policy, not a claim of automated enforcement.

## Sources

* [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/)
