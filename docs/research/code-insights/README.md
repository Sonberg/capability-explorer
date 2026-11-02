# Research evidence foundation

This is the tool-free first package for [issue #31](https://github.com/Sonberg/capability-explorer/issues/31).
It installs no candidate and changes no production extraction or ce.v1 schema.
See [MADR-0019](../../adr/0019-independent-tool-evaluation.md) and the
[council protocol](../../plans/code-insights-tool-council-protocol.md).

```sh
make EXECUTION=sandbox test-research-foundation
node scripts/research/check-code-insights.mjs foundation
node scripts/research/check-code-insights.mjs tool --id scc --stage desk
```

The foundation checks historical arithmetic and a three-file synthetic sealed
corpus with handwritten source anchors. This corpus tests the validator; it is
not sufficient to qualify scc or another analyzer. Tool gates read a dossier at
`tools/<tool-id>/dossier.json`, referenced role briefs and bounded evidence files.
The scc dossier records an independent desk **defer** verdict. Its beta/stable
source mismatch was resolved, but expanded trial fixtures, verified executable,
dependency qualification and execution controls remain absent. No candidate trial
has run. The judge confirmed the transcribed decision and complete packet digest.

Stages are separate: `desk`, `trial`, `decision`, `pr`. A defer/reject/retain desk
verdict can pass record validation but always fails a later trial/adoption gate.
Full-study commands `specification`, `pseudocode`, `architecture`, `refinement`,
`completion` and `all` deliberately fail with an incomplete-evidence message.
Their future executor is outside this foundation; never use foundation success
as a replacement for their missing evidence.

Each proposal binds its source and baseline commits, mode, role, explicit gap,
criteria, required cases, fixed budget and input/oracle references. Four distinct
agents submit role briefs. The judge's packet digest additionally binds claim
references, all briefs, authors, blockers and desk verdict. An introduction
decision binds that packet and the trial receipt, including actual referenced
bytes. PR record checks bind main/head/base and prohibit multiple tools/modes;
the repository's actual `scripts/pr-check` remains authoritative for publication.

No receipt-supplied command is executed. Files cannot escape the supplied root,
follow symlink components or exceed 8 MiB each. The checkout must be isolated and
quiescent; this is not a concurrent hostile-filesystem service. A passing record
is not a signed attestation or proof that a process ran. Coherent fabricated
claims require independent evidence review; hashes only detect changed bytes.

Tests create clearly labelled synthetic receipts in temporary directories and
delete them afterward. They are not candidate runtime results. Existing source
facts, deterministic derivations, model proposals and human assertions remain
separate; current unknown ratings cannot inherit historical desk evidence.

Candidate statuses and PR/dependency links stay on #31. The owner decides merges.
