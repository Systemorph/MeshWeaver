---
Name: Release to Production — the whole path
Category: Architecture
Description: What actually has to happen between a green merge and a working page on a live mesh — the two content channels (plugin install and GitSync), the recompile that both must trigger, the platform image rollout that nothing automates today, and the update policies and conflict model the channels are converging on.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12h4l3 8 4-16 3 8h4"/></svg>
---

# Release to Production — the whole path

> **Merging changes nothing on a mesh.** An instance runs what it has *received* and *last compiled*.
> This page is the map of everything between a green merge and a working page, because each hop has
> its own failure mode and several of them fail **silently**.

## The hops

```
green merge on a plugin repo's main
   │
   ├─► (A) CONTENT reaches the instance
   │      ├─ plugin-install channel — the catalog: webhook → BuildCompletion node →
   │      │  PluginUpdateWatcher → delta install   (see Plugin Update on Green Build)
   │      └─ GitSync channel — a Space's own {Space}/_GitSync, pulled by check/update
   │
   ├─► (B) the affected NodeTypes RECOMPILE          ← the assembly is the deliverable
   │
   └─► (C) the PLATFORM image rolls out (only when core itself changed)
```

**All three are required, and they are independent.** A page can be stale because the content never
arrived (A), because the content arrived but the assembly did not (B), or because the whole portal
predates the fix (C).

## (A) Two content channels, one destination

The catalog channel is described in
[Plugin Update on Green Build](/Doc/Architecture/PluginUpdateOnGreenBuild). The GitSync channel is
described in [GitHub Sync](/Doc/Architecture/GitHubSync). What matters here is that a module can
arrive by *either*, and an instance may use different channels for different Spaces — so "is it
deployed?" is answered per Space, not per instance.

Two operational rules follow:

- **Sync triggers run as SYSTEM.** The trigger writes an Activity node into the partition, and on a
  system-owned (GitSynced) Space no human holds write there. The click *authorizes*; the system
  *executes*. Requiring partition write instead is what made every sync fail with
  `Access denied: Create permission required for {Space}/_Activity/…`.
- **A source query is anchored at its namespace.** See
  [Adding a New Node Type](/Doc/Architecture/AddingANewNodeType): shared source belongs at
  **partition level**. A `shared=` reference into another NodeType's subtree can never resolve from
  a sibling type's compile, because that subtree is served by its own hub — and no disk-based gate
  can see the difference, because they resolve `shared=` from the file tree.

## (B) The recompile is part of the delivery, not a follow-up

Importing nodes does not rebuild anything. Until the affected types are released, the mesh keeps
serving the **last good assembly** — which is why a deployed fix can appear not to work, and why
"it still renders" is never evidence that a deploy landed.

Both channels must therefore recompile, and the set is larger than it looks:

- the type whose own `Source/` changed, **and**
- every type that pulls that file in via `shared=` — a shared file compiles into *each* sharer, so
  one edit leaves as many stale assemblies as there are sharers,
- in **dependency order**, dependencies before dependents.

The delta-install path scopes recompiles to the package; the GitSync path now computes the same
affected closure on import. The closure is the same computation CI uses to decide which modules'
tests to run, so the runtime and the pipeline agree by construction rather than by convention.

## (C) The platform image — the hop nothing automates

Continuous delivery **builds and pushes** the portal, migration and plugin-tester images. It does
**not** roll them out: the deployments pin an explicit image tag rather than following a moving one,
so a published image reaches an instance only when something sets that tag.

That is a deliberate safety property (no unattended platform upgrades), but it is easy to forget:
a core fix can be merged, green, and published, and still be absent from every instance. When a
symptom points at core behaviour, **check the running image first** — comparing the deployment's tag
against the newest tag in the registry costs one command and forecloses a long, wrong hunt.

## Update policy

The **default is automatic**: a plugin repo's green build reaches these portals without anyone
clicking. Per-record overrides exist for the cases where that is wrong:

| Policy | Behaviour |
|---|---|
| **Auto** (default) | apply the update, including the recompiles of (B) |
| **On demand** | surface that an update is available; apply on the click |
| **Ignore** | never apply; stay visible as a suppressed update |

The policy governs *applying*, never *noticing* — an ignored update is still recorded, so an
instance can always answer "what am I behind on?".

## Local changes and conflicts

A two-way synced Space keeps server-side edits on update rather than overwriting them, and the last
synced commit is retained. That gives a genuine three-way basis — **base** (last synced),
**theirs** (the release), **ours** (the local node) — and the intended surface is a per-node review
where a kept-local node is *shown*, with its diff, and resolved deliberately (keep mine / take the
update). The failure to avoid is silence: a node that quietly stayed local is indistinguishable from
one that was never sent, and the divergence is only discovered much later.

## Diagnosing "my change is not live"

Ask the hops in order, and stop at the first `no`:

1. **Did the content arrive?** Check the Space's sync activity, or the install record's version.
2. **Did the types recompile?** Read `compilationStatus` on each affected NodeType, and compare
   `compiledSources` against `currentSourceVersions` — a mismatch means the assembly is older than
   the source, which is exactly the stale-assembly case.
3. **Is the platform new enough?** Compare the running image tag against the registry.

A compile that fails with `CS0246`/`CS0103` on types that plainly exist is almost always a source
**discovery** problem, not a source problem: read the compile activity's source-discovery trace,
which lists the queries it ran and the nodes each matched.

## How a module actually reaches an instance — and how to add a new one

This is the part that surprises people, so it is worth stating plainly:

> **On our instances, modules arrive through per-Space GitSync, not through the plugin catalog.**

On `memex.meshweaver.cloud` there are **37 `{Space}/_GitSync` configs** — one per deployed Space,
each naming a repo and a subdirectory — and the install-records partition holds **no install
records at all**. Every reinsurance and education Space is there because it has a sync entry. A
module with **no sync entry is simply not on the mesh**, no matter how green its repo is. That is
the whole explanation for "the module is merged but I cannot find it".

### Adding a NEW module to an instance

A new module needs a Space *and* a sync entry, and the order matters:

1. **Do not hand-create the Space.** Creating it makes the creator **Admin on a system-owned
   partition** — which the access rules forbid, and which every human-run sync re-mints. See
   [Access Control](/Doc/Architecture/AccessControl).
2. **Provision it** through the install/Store flow, so the Space is created by the *system*
   identity and carries no personal grant. Making the package **install-by-default** is the
   supported way to have this happen without a manual step per instance.
3. **Then** the Space's `_GitSync` is configured (repo + subdirectory + branch), and from that point
   `check` / `update` carry every later change.
4. **Verify by effect, not by log**: read the sync activity's node count, then confirm the affected
   types' `compilationStatus` — see (B) above.

### Updating an EXISTING module

1. Merge on green in the module's repo.
2. Let the update reach the instance — automatically where the catalog channel is wired, or by
   running `update` on the Space.
3. **Confirm the recompiles.** The import alone does not rebuild; check every affected type,
   including `shared=` sharers whose own nodes never changed.
4. If core itself changed, remember hop (C) — the image.

### The checklist that catches the usual failures

- Does the Space have a `_GitSync`? (no entry ⇒ not deployed, and no amount of syncing helps)
- Did the import report the node count you expected?
- Is every affected type `Ok`, and does its `compiledSources` match `currentSourceVersions`?
- Is any new `shared=` reference **partition-level**, not into a sibling type's subtree?
- Does every folder named by `installPaths`, an embed, or a `shared=` have a backing node file?
- If the symptom smells like core behaviour: is the running image new enough?

### Where this is going — auto-discovery per repo

Requiring one entry per Space is why modules go missing, so the configuration is moving **up to the
repo**: a configured plugin repo gains *auto-discover new modules* and *auto-sync discovered
modules* settings, and a build event then provisions any new module as SYSTEM, writes its
`_GitSync`, imports and recompiles it — with no per-Space interaction, gated by the free-vs-
commercial permission rule. Removed modules are surfaced as orphaned, never auto-deleted.
