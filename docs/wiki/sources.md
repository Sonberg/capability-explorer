---
type: Source Catalogue
title: Public qualification sources and retrieval limits
description: Six external requests, retained references and exact source limitations.
status: draft
tags: [sources, research, provenance]
generated: {by: copilot-cli/1.0.105, at: "2026-11-04T01:15:00+01:00"}
sources:
  - id: qualification
    resource: qualification.md
  - id: native
    resource: https://github.com/Sonberg/capability-explorer/blob/ae9bac966037a5348027c2b2d3e0201a430bf09c/schema/README.md
---
# Bounded request register

Retrieval date: 2026-11-04. The independent researchers reported the following
six requests. Failed requests count against the cap. No extra source lookups
were made to repair the failures.[^qualification]

| # | Requested resource | Outcome | Evidence limit |
|---|---|---|---|
| 1 | https://registry.npmjs.org/bpmn-moddle/latest | HTTP 200 | Registry metadata for 10.3.1, not installed package bytes |
| 2 | https://registry.npmjs.org/dmn-moddle/latest | HTTP 200 | Registry metadata for 12.2.1, not source-level namespace behavior |
| 3 | https://raw.githubusercontent.com/bpmn-io/bpmn-moddle/f35959afc443444b706a4f39542530134233db07/src/index.js | HTTP 404 | Guessed source path failed; no code evidence obtained |
| 4 | https://raw.githubusercontent.com/bpmn-io/dmn-moddle/75e2f759fa3e168a2a098358825ede2a0de02282/src/index.js | HTTP 404 | Guessed source path failed; no code evidence obtained |
| 5 | https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.flowanalysis.controlflowgraph?view=roslyn-dotnet-4.9.0 | HTTP 200 | API description, not executed compiler behavior |
| 6 | https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.flowanalysis.basicblock?view=roslyn-dotnet-4.9.0 | HTTP 200 | API description, not emitted graph shape |

No fetched-response byte digests were retained. Registry artifact SHA-1 metadata
is not a digest of the fetched JSON. The Learn responses reported backing docs
commit `ab1083958d076ae5dc06e83965160204f830cb68`; that is reported metadata,
not an independently verified immutable capture. Mutable URL responses and
worker reports do not establish exhaustive conformance.

# Local and retained references

Public code/schema inspection is bound to the stage-1 base
`ae9bac966037a5348027c2b2d3e0201a430bf09c`; documentation changes do not
alter those inspected files. Publication is based on newer public main; source
claims here retain their original scope rather than being relabeled. Native
evidence conventions come from the
[pinned contract guide](https://github.com/Sonberg/capability-explorer/blob/ae9bac966037a5348027c2b2d3e0201a430bf09c/schema/README.md).[^native]

The [maintenance page](maintenance.md) cites retained OKF and LLM Wiki readings;
they were not new retrievals in this six-request round. No new OMG, viewer
license or package-source behavior claim was established here.

Private local provenance stays outside this public corpus. A public source
catalogue is not permission to redistribute other material. Read the
[qualification](qualification.md) for what each observation actually supports.

[^qualification]: Integrated researcher reports, bounded four-plus-two request allocation; two HTTP 404s preserved as gaps.
[^native]: Native contract guide at the source base identified above; not a new export-profile implementation.
