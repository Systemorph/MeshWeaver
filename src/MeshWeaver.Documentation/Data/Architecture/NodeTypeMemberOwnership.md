---
Name: Who Owns a NodeType Member
Category: Architecture
Description: Every member of a NodeType's definition is owned by the repo or by the mesh, and the mask that encodes it was pinned in one direction only — so four runtime-state members were missing from it, one of them spelled outside the naming convention that was supposed to catch them.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2 4 6v6c0 5 3.4 9.4 8 10 4.6-.6 8-5 8-10V6l-8-4z"/><path d="M9 12l2 2 4-4"/></svg>
---

# Who Owns a NodeType Member

A NodeType node's content is a `NodeTypeDefinition`, and it has two owners at once. The **repository**
owns the authored definition — `Configuration`, `Sources`, `Description`, `Dependencies`. The **mesh**
owns the compile bookkeeping the framework writes as the type builds — the verdict, the timestamps,
the assembly coordinates, the source-version maps, the release triggers. One record, two owners, and
three seams where a node crosses between them:

| Seam | Rule | Where |
|---|---|---|
| **Export** | strips the mesh-owned members | `NodeTypeOperationalContent.StripOperational` |
| **Import / upsert** | preserves the LIVE node's values, absent when live has none | `PreserveLiveOperational`, applied by the owner inside the upsert merge |
| **Change detection** | ignores them — a node differing only in bookkeeping has not changed | `PartitionSourceFingerprint` |

All three read `NodeTypeOperationalContent.MemberNames` — the two strip rules over the union with
`StrippedButNotPreserved` (see below), the preserve rule over `MemberNames` alone. Getting a member
into the right list is the whole of the ownership decision; leaving one out is silent in every
direction.

Why it matters is on [Node Type Compilation](../NodeTypeCompilation) and in `ShippedNodeTypeStateTest`:
a node that adopts a compile verdict it did not earn on THIS deployment is unreachable by every
automatic compile path, and parks forever on a foreign machine's answer.

## The gap: a set pinned in one direction (#4480)

The list was pinned to the record by one assertion:

```csharp
foreach (var member in NodeTypeOperationalContent.MemberNames)
    Assert.True(properties.Contains(member), "…the list drifted from the record.");
```

That is `MemberNames ⊆ the record`. It catches a mask entry that names no property — a typo, a
rename — and it is **structurally unable** to see a runtime-state property that is MISSING from the
mask. That is the direction that loses data, and it stayed green through four omissions:

| Member | What it decides | What an authored value does |
|---|---|---|
| `LatestAssemblyMvid` | the IDENTITY of the bytes the build produced; bind time compares it against the bytes actually served | forges a MATCH and turns off the stale-build detector (the state in #2471: a portal serving stale compiled code while reporting `Ok`), or forges a MISMATCH and refuses a correct bind |
| `CompiledModulesHash` | `HasUsableBuild` invalidates a build stamped with a different non-null hash than the live module set | a hash matching the importing deployment declares a FOREIGN build usable and suppresses the recompile a module update requires |
| `CompiledDependencies` | the per-type dependency record; `HasUsableBuild`, the bake probe's `Classify` and `IsAlreadyAdopted` all validate it against the live environment | a record matching the bundle makes a FRESH install read as already-adopted — the bytes are never seeded and the type parks on a stamp nobody earned |
| `DispatchedBuildInputs` | what the compile IN FLIGHT was dispatched for; a request resolving to the same token is CONSUMED rather than queued | a token matching a live request absorbs that request against a compile nobody dispatched, and the release it asked for is lost |

The first three leaked in both directions: export left them in the repo file (and in the change
token, which then moved when only the runtime state had changed), and import let a file overwrite a
measurement taken on this mesh. `LatestAssemblyMvid` was incoherent on its own terms — the file named
bytes by an identity that exists nowhere on the importing mesh, while carrying no path to them.

## Why a naming convention cannot be the guard

`ShippedNodeTypeStateTest` bans runtime state from committed files by a **naming convention** —
`Compilation*`, `Compiled*`, `LastCompil*`, `LatestAssembly*`, `RequestedRelease*`, `Failed*` and so
on. It is fail-closed by name, which is exactly right for what it does: a member added tomorrow is
banned the day it is added, with nobody maintaining a list.

It is a *sufficient* condition and never a necessary one. `DispatchedBuildInputs` is compile-pipeline
state spelled outside it, and so are `BuildProvenance`, `ReleaseNotes`, `RequestedSourceStampAt` and
the whole `Adopted*` family. A reverse assertion built on the convention alone would have found three
of the four and been blind to the fourth — the same defect, one name away.

So the guards are three, and each covers what the others cannot:

1. **`MemberNames ⊆ the record`** — a mask entry that names no property.
2. **convention ⊆ `MemberNames`** — a member SPELLED as runtime state that nobody masked.
3. **the partition** — every serialised member of the record is classified as repo-authored,
   mesh-owned-and-masked, or stripped-but-not-preserved. Exactly one bucket, no member
   unclassified. This one does not depend on what a member is called, so a new property cannot be
   missed however it is spelled — and its denominator is the `JsonIgnoreCondition`, never the
   presence of a `[JsonIgnore]` attribute: `IncludeGlobalTypes` carries one with
   `Condition = Never`, which means it is ALWAYS written.

The convention lives in **one** place (`NodeTypeMemberOwnership` in the Graph test suite) and every
guard reads it from there. Two lists that both approximate the same set drift pairwise; that is the
failure this page describes, one layer up.

## The third bucket: stripped from every file, never preserved on import

The three seam rules are not one switch. A member can belong to the mesh at the *export* and
*change-token* seams and still have to lose at the *import* seam — and `PendingRetirement` is
exactly that, so it lives in `NodeTypeOperationalContent.StrippedButNotPreserved` rather than in
`MemberNames`:

- it is stamped by a repository-driven import when the repo retired a type that still has live
  instances, through the probe's own `stream.Update` — never through an upsert, so masking would
  not protect the write;
- **nothing in `src/` ever writes null back to it.** The one thing that clears it is the repo
  shipping the type AGAIN: an upsert replaces the node's content wholesale, so a member the live
  node holds and the file does not simply goes away.

Mask it and a re-shipped type stays marked retired forever — and the bake gate reads a stamped type's
compile failure as `Retired`, i.e. as a verdict that must *not* hold a rollout.

**But it is still runtime state, so a file must never carry it either.** Leaving it out of the
*strip* as well would let the live stamp ride an export straight into the repository — GitSync's
`SerializeOne` exports through `StripOperational` — and a file that carries it can then forge a
retirement the mesh never measured. So the strip paths (export, the typed import reset, and the
change token) use the **union** of the two sets, and only the preserve path uses `MemberNames`
alone. That asymmetry *is* the third bucket, and it is what the entry's reason has to justify.

## What to do when you add a member to `NodeTypeDefinition`

Decide the owner, and write the reason down next to the entry:

- **The repo owns it** → add it to `NodeTypeMemberOwnership.Authored`. Nothing else to do; imports
  honour it and the change token sees it.
- **The mesh owns it** → add it to `NodeTypeOperationalContent.MemberNames`, with a comment saying
  what an authored value would forge — every entry there carries one. It must also be added to
  `NodeTypeCompileState`, which carries exactly the masked set (pinned by
  `NodeTypeCompileStateTest`), or the compile-state satellite silently drops it.
- **The mesh writes it but the import must NOT preserve it** →
  `NodeTypeOperationalContent.StrippedButNotPreserved`, with the reason why dropping it on re-import
  is the point. It is still stripped from every file shape, so nothing extra is needed to keep it
  out of the repository.

The partition guard fails until one of the three is true, and names the member.

## The installer's unchanged-gate reads the same partition

"Imports honour it" has one more seam than the merge. A package re-install writes a NodeType node
only when `PackageInstaller.IsUnchanged` says the file differs from the stored node, and that gate
runs **before** the upsert is asked. A gate that compares fewer members than the repo owns turns
every member it leaves out into one that can be set at the first install and never changed again.

That is what it did. The gate compared `Configuration`, `Sources` and `InstanceLocations` and
nothing else, so a file that changed any other authored member read as unchanged and was never
written. Measured on a deployed portal: `Governance/NameCheck` declares `"keepsHistory": false` in
its committed file, and its stored definition read `keepsHistory: true` at node version 143, across
five minor releases of the package. The versions read back were the mesh's own compile bookkeeping
(whether an import ever wrote the node after its first install was not established). The serializer fix that lets an
explicit `false` survive a write (`[JsonIgnore(Condition = Never)]` on the flag) could not help,
because no write was issued. Nineteen authored members were invisible to the gate this way.

The gate now compares the **whole authored surface**: both definitions are stripped with
`NodeTypeOperationalContent.StripOperational` — the strip an export applies, so "what a file may
carry" and "what a re-install compares" are one set — and their canonical signatures are compared.
Nothing is listed in the installer, so a member added to the record is covered the day it is
classified here.

`AReinstallLandsEveryAuthoredNodeTypeMemberTest` pins it reflectively: for every serialised member
the mask does not name, a file that differs in that member alone must read as a change. Its two
controls are the half that keeps a re-install idempotent: a stored node that differs from its file
only by compile state must still read as unchanged, and so must an identical definition.

**What this does to a running fleet.** The first install after this change writes each NodeType
whose stored authored members had drifted from its file, once, and requests its compile. A type
whose file and stored definition already agree is untouched. For a type that declares
`keepsHistory: false` the version writer's cached verdict is invalidated by that write, and history
written before it is purged by the next write or the delete of each instance.

## A wider gate is reached only if the install compares at all

Widening the gate was not enough on its own. Measured on a deployed portal: the Governance package
was reinstalled on a platform carrying the wider gate, and `Governance/NameCheck` still read
`keepsHistory: true`. The gate was never consulted.

The install funnel (`CatalogLayoutAreas.InstallOrUpdate`) decides from the install record before
any node is read, and both of its shortcuts read hashes of the **source**:

- the up-to-date exit returns when the record's module hash equals the served one;
- the incremental update fetches only the files whose hash moved since the record.

Each treats the record as a statement about the **mesh** — every file at this hash is stored as the
package ships it. That statement is only as good as the comparison that decided each write. The
install that met `keepsHistory: false` under the narrow gate skipped the write and stamped the
file's hash anyway, so every later install found an equal hash and never compared the node again.

The record therefore carries the rule it was stamped under, `PackageManifest.ComparisonEpoch`, and
the installer names the rule it applies, `PackageInstaller.InstallComparisonEpoch`. A record under any
other rule is no baseline (`PackageInstaller.RecordIsNotABaselineUnderThisRule`): the funnel installs
in full, which compares every stored node with the package, writes only the ones that differ, and
stamps the installer's own rule.

The match is exact, never "at least". During a surge-first roll an older pod and a newer one share
the install records. Were a newer rule's record a baseline for the older pod, its incremental update
would compare under the older rule, skip a difference only the newer rule sees, and carry the newer
value forward — a record falsely claiming the newer comparison, which the newer pod's equal-hash exit
would then never repair. Under an exact match the older pod installs in full and stamps its own rule,
and the newer pod, finding a mismatch, compares in full again under its own. Only a full install may
stamp it; an incremental update compared only what it fetched and carries the previous value
forward.

**Raise `InstallComparisonEpoch` whenever the gate starts to see a difference it used to miss.**
Every record stamped before that change claims content the old gate never compared. Narrowing the
gate needs no bump.

`ARecordUnderAnOlderComparisonRuleIsNoBaselineTest` re-enacts the measured state through the real
funnel: a stored node differs from the package while its file hash is unchanged, the record carries
no rule, and the reinstall must rewrite the node. Its control is the same sequence under a record
stamped by the current rule, where the up-to-date exit is trusted and nothing is written.

**What this does to a running fleet.** Each package does one full install the next time a lane
reaches the funnel for it: the boot pass for the platform baseline and the environment's packages, a
person's Install or Update, or the next update of the package. The full install writes only the
nodes that differ; a package whose stored nodes all match its files writes nothing and recompiles
nothing. A package installed by hand whose source does not move is reached by no lane until someone
reinstalls it.

## Related

- [Node Type Compilation](../NodeTypeCompilation) — the compile control plane that writes this state
- [CQRS and Content Access](../CqrsAndContentAccess) — why the live node, not a query, is the authority
- [Static Repo Import](../StaticRepoImport) — the seam where a repo file becomes a node
