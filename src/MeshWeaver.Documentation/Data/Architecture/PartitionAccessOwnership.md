---
Name: Who Owns a Partition's Access Shape
Category: Architecture
Description: Two components write a plugin partition's _Policy and its children's _Access denies — the installer in this repository and the Store's gating reconcile, which is in-mesh source in another one. When both claimed ownership they undid each other on every pass, and the ping-pong failed a CD seal. The rule that settles it, and the survey mistake that produced it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><path d="M9 12l2 2 4-4"/></svg>
---

# Who Owns a Partition's Access Shape

**A partition's `_Policy` node and its children's `_Access` deny assignments have exactly ONE
owner. Where two components both wrote them, neither converged, and the loser was the platform.**

Two components write that shape today:

| Writer | Lives in | Reads | Runs |
|---|---|---|---|
| `PackageInstaller.EnsureDeclaredAccess` | this repository, `src/MeshWeaver.PluginCatalog/` | the package **manifest** (`preInstalled`, `price`, `publicSegments`) | once per install, and on the boot repair pass |
| `PluginGate.SeedGating` | MeshWeaver.Plugins, `Store/Licensing/Source/` — **in-mesh source** | the **root node's** `PluginContent` | on every plugin-root activation, and on every subtree change |

For a **pre-installed** partition the two agree: the installer publishes it fully public, and the
gating reconcile's pre-installed arm retracts child denies. Nothing fights.

For a **free, non-pre-installed** package that declares no `publicSegments`, they used to disagree
outright — and both were emphatic about it in their own comments:

> *the installer:* "a free package that a catalog hands out must be readable by everyone, signed in
> or not."

> *the gating reconcile:* "the cover + declared public segments are the ONLY public surface … there
> is no open-content tier: a product without a price simply has no self-service way in yet. (The old
> rule left non-purchasable plugins' content readable — guides, dashboards, even imported personal
> LinkedIn data were world-visible via the cover grants.)"

## What the disagreement cost

The installer's legacy heal treated the pairing *policy-withholding-public-read + Public/Anonymous
child denies* as damage left by a pre-#902 installer, retired the denies and reopened the policy.
That pairing is precisely what the gating reconcile writes, on purpose, every pass. So each pass of
each component undid the other:

- the installer retires N denies and opens `_Policy`;
- the gating reconcile re-denies and re-gates, logging
  `[PluginGating] <plugin>: reconcile is NOT CONVERGING — rewrote …, which this hub already wrote`;
- the next install repeats it.

Measured on CD run `34190841613` (2026-09-08), package `Chess`: the idempotence re-install retired
**26** deny assignments, the reconcile immediately rewrote them, and the heal's own `Chess/_Policy`
write starved behind the contention for 20 s and faulted —
`MeshNode Unknown at 'Chess/_Policy': TimeoutException`. That failed the package-install idempotence
gate, so `Plugins: bake + seal` went red and `Register the publication with memex` never ran: a
complete image set was promoted and verified, and **no installation could adopt it**, because a
promoted set that is not sealed is held.

**The run 65 minutes earlier, on the same commit, passed while doing the same thing at a smaller
amplitude — 9 denies retired instead of 26.** Identical content, different count. A steady state
that differs run to run on unchanged inputs is not a steady state; it is a race, and the count is
just how far one writer got before the other looked.

`Chess/_Policy` has a longer history of write storms — measured at **version 210,801** on
2026-08-25 and `Chess/_Access/Public_Access` at **11,899** on 2026-08-09 — but those are **not**
this defect and should not be cited as it: `PluginGate`'s own comments attribute them to the
reconcile writing off blind subtree snapshots, which the targeted-read `VerifiedWrite` change then
fixed. They belong here only as evidence that these particular nodes are a contended surface with
more than one way to loop, which is the reason to give them exactly one owner.

## The rule

**A partition that is not pre-installed belongs to the gating reconcile. The installer does not
heal it.**

The installer still *creates* a fully-public `_Policy` where none exists — a create cannot loop —
and still heals the legacy shape on **pre-installed** partitions, which is the case the #902
incident was actually about ("its 8 pre-installed partitions carried 136 legacy denies, while the
partitions installed after #902 were correct"). What it no longer does is tear down denies that a
live component is writing on purpose.

This does not settle *whether* a free plugin should be world-readable. It settles **where that
question is answered** — in one place, so that the answer can be changed by changing one rule
rather than by winning a race. If the gating model is wrong, it is wrong in one component.

## The survey mistake, which generalises

The heal's guard was not the pairing alone. It was the pairing plus a claim:

> "Current code cannot produce that (the scoped branch requires `declared.Count > 0`); only a
> pre-#902 installer could."

That claim was reached by reading this repository's code, and it is true of this repository's code.
The component that produces the shape is **in-mesh source**: a `.cs` node stored in a mesh package,
compiled at runtime in the portal. No `dotnet build` here compiles it, no core test executes it, and
`grep --include='*.cs'` over this repository cannot see it. The survey was complete for the
compiler's view of one repository and empty of the thing it needed to find.

**When a heal, a migration or a guard is justified by "no current code produces this", the survey
has to cover the MESH — every node repository's `Source/*.cs`, every NodeType `configuration`
lambda, every layout area — not one repository's compiler view.** `AGENTS.md` states this for
deletions of public surface; it applies identically to any premise of the form *nothing writes
this any more*.

## Related

- [Access Control](/Doc/Architecture/AccessControl) — partition policies, assignments, and the Admin partition.
- [NodeType Compilation](/Doc/Architecture/NodeTypeCompilation) — why in-mesh source is invisible to CI.
- [Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract) — what a sealed publication is, and why a promoted-but-unsealed set is held.
