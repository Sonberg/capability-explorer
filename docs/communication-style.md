# Plain technical English

## Decision and scope

The operator authorized this project-local guidance on 2026-11-05 after an
adviser, adversary and judge evaluated the proposal. Repository-wide adoption
remains subject to contributor review. This is **STE-informed plain technical
English**, not a claim of conformity with ASD-STE100 or ISO 24495.

Use this guide for new or edited issues, comments and agent chat responses.
Also apply it to new or edited PR descriptions, reviews, handoffs and documentation.
Keep short messages short; not every response needs headings or a template.
Existing issue history does not need a rewrite.

Generated explanations, UI labels and error messages need a separate scoped
change and behavioral validation before adoption. This guide changes no code,
wire identifiers, schema, API or policy meaning. It grants no action, approval,
publication or release authority. Follow higher-priority instructions and explicit
language or format requirements; adapt the clarity principles where applicable.
Do not install global output styles or change another contributor's settings.

### Why this approach

Controlled terminology and direct sentences can reduce ambiguity. A blanket
restricted vocabulary or "explain like I am five" style can instead remove
necessary distinctions. Preserve technical precision and simplify the prose
around it. Terminology and prose style are separate: this guide changes how
ideas are expressed, not what project terms mean. The
[project glossary](ubiquitous-language.md) and
[MADR-0002](adr/0002-ubiquitous-language.md) govern shared meanings, subject to
the executable contracts. If they conflict, report the conflict; do not silently
change a contract or redefine a term.

Name the model or context when a term could mean different things. For example,
the project's technical **Capability** selects repositories for analysis; it
does not mean a business ability. Model ownership, process grouping and
deployment are separate concepts. Provenance is not approval, and a concept
mapping is not record identity or a runtime connection. These are local modeling
distinctions, not universal standards.

The council recommended a bounded pilot before broad adoption. Its roles shared
sources and model lineage; agreement is not independent validation or contributor
acceptance. The [pilot](research/communication-style-pilot.md) records synthetic
examples, review criteria and evidence limits. Keep human acceptance separate.
This guide holds the writing rationale without changing the separately owned
ADR ledger or accepting any existing proposal.

## Writing rules

1. Lead with the answer, verified outcome or actual blocker. Do not start with
   "done" when work is partial, blocked or unverified.
2. Use short, direct sentences. Prefer one main point per sentence and one
   action per procedural step. Keep a condition with the action it limits;
   place a prerequisite before an action that depends on it.
3. Prefer active voice when the actor is known. When the actor is unknown, say
   so or use passive voice. Do not invent responsibility.
4. Use the same defined term for the same concept. Name the model or context
   when a term is overloaded or its meaning is not obvious. Explain an unfamiliar
   acronym on first use when the reader needs it. Keep necessary technical nouns,
   verbs, names and acronyms; direct prose does not require replacing them with
   vague or less precise words.
5. Preserve conditions, negation, quantities, units, actor, scope and obligation.
   **Must** states a requirement, **should** a recommendation, and **may**
   permission where that is the intended meaning. Do not silently change a
   source's normative terms; resolve ambiguous uses of "may" or "can" explicitly.
6. Preserve uncertainty and evidence limits. **Unknown** does not mean failed or
   safe to retry. **Candidate** does not mean confirmed. Partial observation
   does not establish absence. Tested, reviewed, approved and authorized are
   different claims. Necessary qualifications are not needless hedging.
7. Keep literal code, commands, filenames, identifiers, logs and necessary
   quotations exact when they are needed and safe to share. Explain them
   separately. Do not edit evidence to make it appear clearer or more favorable.
8. Protect confidential information. Neutral names alone do not make a log or
   example safe to publish. Remove or withhold secrets, private URLs and sensitive
   source details before sharing; label a redacted excerpt as redacted. Prefer
   synthetic examples. Do not send private text to external writing services.
9. Include enough detail for the reader to act correctly. Do not use analogies,
   sentence-length limits, readability scores or shortness as substitutes for
   factual accuracy. Use tables or lists only when they help the reader.
   Concern categories can help organize a response, but they are not a mandatory
   answer template.

The operator's "up to 80%" phrase is an informal adaptation for ordinary prose.
It is not a measured compliance rate, quota, readability score, or formal
ASD-STE100 conformance or certification. Apply the principles without changing
technical meaning; do not claim that a percentage measures compliance.

## GitHub issues

Use a specific, outcome-oriented title, such as
"Keep candidate targets separate from reviewed connections".
Use the [bug form](../.github/ISSUE_TEMPLATE/bug_report.yml) for a reproducible
defect and the [change form](../.github/ISSUE_TEMPLATE/feature_request.yml)
for a requested outcome. These are authoring aids, not enforced semantic checks.
API-created issues and free-form issue bodies need the same review.

Use only the sections the issue needs:

```text
Problem or goal: What needs to change, and why?
Current and expected behavior: What happens now? What must happen instead?
Scope: What is included? What is excluded?
Acceptance checks: What observable result will verify the change?
Evidence and dependencies: What public-safe evidence or prerequisite matters?
```

For bugs, include reproduction steps and relevant environment or revision.
Distinguish observations from suspected causes. Do not invent reproduction
details or measurements. If a target is not decided, mark it as undecided and
name the decision needed rather than supplying a fabricated number.
Keep strict definitions, cardinalities, source qualifications and acceptance
detail in complex issues. Add a clear summary rather than deleting that detail.
Follow existing measurable-objective and work-claim requirements where applicable.

## Chat, reviews and handoffs

Lead with the result or blocker. Add the meaningful change and actual
verification. Include a material limitation or required action when one exists.
Do not repeat the request, narrate routine tool use or offer unnecessary extras.
Use no fixed headings for a one-sentence answer.

For example:

> The parser tests passed. They cover accepted input, not authorization.
> Authorization remains unverified.

For a blocked operation:

> Analysis remains blocked because the previous run's final state is unknown.
> I did not clear the block or start another run.

A handoff still needs the exact source state, owner, validation, unresolved work
and ownership release required by the working agreement. Do not remove these
to make the handoff shorter. Ask for input only when a real decision is needed,
using the host tool's supported interaction method.

## Review and adoption

Before sharing, check that the reader can identify the result, relevant actor,
conditions, evidence and next action without guessing. Check glossary meanings,
uncertainty, obligations and privacy independently of sentence length.

Run the pilot before proposing broad contributor adoption. Require zero material
changes in meaning, obligation or privacy. At least six of eight revised samples
must be equally clear or clearer, with none materially less clear. This is a
screening threshold, not measured project-wide effectiveness. Record reviewers,
disagreements, source inputs and remaining limits.

Do not add a mandatory word-count, readability or dictionary gate. Optional
future lint must be advisory, respect technical terms and avoid external text
uploads. No checker or prompt establishes conformity with a standard.
Changes to terms or obligations need their existing review process, not an
automatic style rewrite. Revisit this guidance if reviews find lost meaning,
excessive repetition, false certainty or poor agent instruction discovery.

## Sources

- [8West article](https://8west.ie/insights/make-claude-talk-aircraft-manual-language-asd-ste100):
  motivation for clearer agent responses, not evidence that a prompt enforces STE.
- [Official STE overview](https://www.asd-ste100.org/about_STE.html):
  writing rules and controlled vocabulary permit subject-specific technical terms.
- [Official STE FAQ](https://www.asd-ste100.org/STE_faq.html):
  principles can support other writing; tools are optional; precision is not childishness.
- [Official STE software guidance](https://www.asd-ste100.org/STEsoftware.html):
  ASD/STEMG does not certify software; tools are fallible and external services
  can create privacy risks.
- [ISO 24495-1 public scope](https://www.iso.org/standard/78907.html):
  plain-language principles also apply to technical documents. No ISO conformity
  assessment was performed.

Sources were checked on 2026-11-05. This guide does not reproduce the standard's
dictionary or install third-party prompt packages.
