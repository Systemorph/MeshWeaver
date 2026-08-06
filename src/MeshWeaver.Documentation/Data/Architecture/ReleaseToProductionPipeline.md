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
