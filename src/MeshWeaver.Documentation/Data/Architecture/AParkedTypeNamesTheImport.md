---
Name: A Parked Type Names the Import
Category: Architecture
Description: An import that cannot write one source node leaves the partition referenced-but-incomplete, and every NodeType built from the files that DID land fails on CS0246 for a symbol whose file is plainly in git. How the compile failure is joined to the import that produced the state, why the join is scoped to the types that actually reference the missing node, and the three answers it keeps apart so an import is never accused of a deletion it did not make.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/><path d="m9.5 12.5 5 5"/><path d="m14.5 12.5-5 5"/></svg>
---

# A Parked Type Names the Import

A [static-repo import](/Doc/Architecture/StaticRepoImport) that cannot write one source node leaves
the partition **referenced-but-incomplete**. The files that *reference* the missing symbol land
perfectly well, so every NodeType built from them fails to compile — and the only thing an operator
sees is

```
CS0246: The type or namespace name 'SelfUpdateRouting' could not be found
CS0103: The name 'SelfUpdateRouting' does not exist in the current context
```

on a symbol whose file is **plainly in git**.

[A Content Verdict Is Per Node](/Doc/Architecture/AContentVerdictIsPerNode) fixed the import half:
the refusal is remembered per node instead of freezing the partition, the reason travels with it,
and the sync activity names the file at `Warning`. An operator who reads the **sync activity** now
learns which file is missing. This page is the other direction — the operator who starts from the
**compile error**, which is where the symptom actually appears.

## What it cost

**Measured on `memex.systemorph.com`, 2026-09-15.** One file —
`Hosting/Deployment/Source/SelfUpdateRouting.cs` — contained a literal NUL byte (0x00), which
PostgreSQL cannot store in a text column. That one row was refused while forty landed. Five Hosting
NodeTypes parked on the symbol it would have declared, and **only one of them owned the file**:

| parked type | owns the refused file? |
|---|---|
| `Hosting/Deployment` | yes |
| `Hosting/Backup` | no — draws on it through a shared source query |
| `Hosting/InstanceAction` | no |
| `Hosting/InstanceRequest` | no |
| `Hosting/PlatformBuildInbox` | no |

`Hosting/InstanceAction` is the control instance's entire action surface, so while it was parked
*no instance action could run at all* — no `Sample`, no `Logs`, no `HelmRelease`, no `Roll`. Fleet
operations were offline, and the visible symptom was a compile error about code that is fine.

## The join

Everything the fix needs was already recorded. The partition's per-node **import manifest**
(`{Partition}/_Activity/import-manifest`) carries a refused node as `!{token}|{reason}`, which is
the standing answer to *"which declared nodes is this partition missing, and why?"* — a read, not an
inference.

The compile pipeline cannot reach the importer: `MeshWeaver.Graph` references
`MeshWeaver.Compiler.Pipeline`, not the other way round. So the question travels over a one-bit-style
seam, exactly as [the source-tracking question](/Doc/Architecture/NodeTypeCompilation) does:

```
IPartitionImportRefusals            (MeshWeaver.Graph.Contract)  — the question
  └─ StaticRepoImportRefusals       (MeshWeaver.Graph)           — reads the manifest
        ↑ asked by
     ImportRefusalDiagnosis         (MeshWeaver.Compiler.Pipeline) — the join + the sentence
```

A refusal is offered as the explanation of a compile failure only when **both** halves are
established from facts in hand:

1. the partition's bookkeeping **records** a refusal for a node that is a C# source file — the read
   answered, and the refused path carries a `Source` or `Test` path segment, so it is a file that
   declares symbols; **and**
2. **this** compile failed with a name-resolution diagnostic — `CS0246`, `CS0103`, `CS0234`,
   `CS0426`, `CS0400` — that names that node's identifier **as a whole word**.

Either half alone reports nothing at all.

### Why the identifier, and never the message

The join reads the refused node's own id out of its path and looks for it *inside* the diagnostic —
never the reverse. Roslyn's message text is **localized** and its wording is not a contract; a C#
identifier is neither, and the diagnostic ID is a wire identifier that is never translated. The join
therefore holds on a portal running in any language.

The evidence is read **per diagnostic**, not as one flat haystack: the ID and the identifier must
come from the *same* row, so an unrelated `CS0246` elsewhere in a transcript cannot lend its ID to a
mention of the symbol somewhere else.

## The granularity trap, stated as a rule

> Refusing to compile — or accusing — a whole partition because ONE unrelated node was refused would
> re-create exactly the over-broad reading #4459 was about, one layer up.

A symbol can be unresolved for three reasons that look identical from the compile:

- an import lost the file;
- the file was **legitimately deleted**;
- the module carrying it **is not loaded on this replica** — a declined bundle, which is
  [a different defect with a different fix](/Doc/Architecture/NodeTypeCompilation).

Saying *"an import dropped this"* when it did not is **worse than silence**: it sends the operator to
the repository for a file that was never there. That is why the join is scoped to the types that
actually reference the missing node, and why the pinning test compiles a *second* type in the *same*
partition, under the *same* recorded refusal, failing on a *different* unresolved name, and requires
it to be told nothing.

## Three answers, never two

`NodeTypeDefinition.CompilationImportRefusals` carries the structured finding under the same rule
`FailedSourceQueries` keeps:

| value | meaning |
|---|---|
| `null` | **NOT DETERMINED.** No failure verdict stands, the failure was not about an unresolved name, the mesh keeps no import bookkeeping, or the read did not come back. It never means "no import lost anything". |
| empty | **Determined.** The bookkeeping was read and explains none of these names — look at a deliberate deletion, or at a module that is not loaded here. |
| non-empty | An import **recorded** a refusal for these source nodes and this compile failed on the names they declare. The repair is in the repository. |

The read keeps the distinction at the instrument: it uses `GetMeshNodeOutcome`, not `GetMeshNode`,
so `Absent` (a partition whose import recorded no refusal — a real answer) and `Unavailable` (a
budget that elapsed, a fault, a denial — nothing established) cannot collapse into one another.

🚨 And the **parse** keeps it too, which is the easier half to lose. The importer's own manifest
parse degrades every failure to an empty map on purpose — for *it*, "unreadable" and "nothing
recorded" both correctly mean *do a full, non-incremental pass*. For a reader of the refusal ledger
they are opposite answers: a manifest node that exists but is corrupt, reported as the
determined-empty set, would claim the partition's import lost nothing **on the evidence of a parse
failure**, and would suppress the attribution at exactly the moment the bookkeeping is broken. Both
callers share one parse (`TryParseManifest`), so they can never drift about what a manifest *says* —
only about what an unreadable one *means*.

## Where it surfaces

- **`NodeTypeDefinition.CompilationError`** — the sentence LEADS the recorded error, ahead of the
  Roslyn diagnostics, for the same reason the missing-source-query line does: a reader who is not
  told the file is missing spends the investigation hunting the symbol through module surfaces that
  never carried it. This is what `get_diagnostics` returns, what the Settings → Progress error page
  renders, and what `search 'content.compilationStatus:Error'` finds.
- **The compile activity** (`{Type}/_Activity/compile-…`) — the same fact as a catalog-keyed log line
  at `Error`, so an operator reads it in their own language. The path and the reason ride as
  arguments, because neither is translatable.
- **`NodeTypeDefinition.CompilationImportRefusals`** — the structured half, queryable.

🚨 **A park must not erase it.** This is the state an operator actually finds the system in: the
compile that formed the verdict ran once, minutes or hours ago, and every later access is
short-circuited through the park gate with the registry's remembered error — the bare Roslyn text.
`ApplyGateSettle` therefore **re-composes** the sentence from `CompilationImportRefusals` (which
survives the settle by construction) rather than remembering it as prose.

🚨 **…but only for the verdict that earned it.** `formedUnderLiveInputs` separates the two gate call
sites exactly: `false` is the parked short-circuit *re-serving the remembered compile failure* — the
same verdict the finding belongs to — while `true` is a **new** verdict formed then and there (a
delivery hold, an incompatible adopted build), which the finding says nothing about. A source change
un-parks a type *without* clearing the field, so prepending the finding to a later bundle or
availability reason would attach an import diagnosis to a failure that has nothing to do with an
import. A new verdict therefore clears the field as well as declining to quote it. And the field is
cleared in **every** path that clears `CompilationError`/`CompilationDiagnostics` — compile success,
the delivery hold's serving state, prebuilt adoption, the hydrate short-circuit — so a type can never
go `Ok` while retaining a finding for a later settle to pick up.

The member is registered as operational (`NodeTypeOperationalContent.MemberNames`) and carried by
`NodeTypeCompileState` (whose satellite write is [retired](../CompileStateSatelliteRetired)), like every other runtime compile field: it is a
measurement taken against *this* mesh's import bookkeeping, so an authored copy would accuse an
import that never ran here, and an export/re-import would otherwise lose the finding.

The `CompilationError` string itself is deliberately **not localized**, consistently with its
neighbour: it is baked into the node at write time and read back later by tools and by `search`, so
there is no viewer to follow at the moment it is composed. The line an operator *reads in their own
language* is the activity's.

## What this does not cover

- **A refusal recorded before reasons were kept** reports the path and says *"the refusal predates
  reason recording"* — a different sentence from "no reason", and said as such. Any edit to the file
  moves its token and the next import records the reason. The import activity has a **separate
  catalog key** for the with-reason wording rather than a new argument on the old one: activity log
  messages are persisted *with their arguments*, and adding a placeholder to an existing template
  makes every historical row render a literal `{reason}` for ever, because the catalog deliberately
  leaves an unknown placeholder visible. A second key is the only shape that can carry a new
  argument without rewriting the past.
- **The install path.** MeshWeaver#4259 is the same *shape* on a package install — declared source
  nodes that never landed, with nothing logged. Whether this reporting should be mirrored there is
  open.
- **A symbol missing for one of the other two causes** is still reported exactly as it was: the
  diagnostics, and nothing added. That is the point.

## Related

- [A Content Verdict Is Per Node](/Doc/Architecture/AContentVerdictIsPerNode) — the import half, and the incident
- [NodeType Compilation &amp; Releases](/Doc/Architecture/NodeTypeCompilation) — the compile lifecycle this diagnosis rides on
- [Static Repo Import](/Doc/Architecture/StaticRepoImport) — the pipeline the manifest sits in
- [Syncing a Space with GitHub](/Doc/Architecture/GitHubSync) — the route that fetches, imports and reports
