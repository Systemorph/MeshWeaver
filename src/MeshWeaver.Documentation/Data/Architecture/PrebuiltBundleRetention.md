---
Name: Prebuilt Bundle Retention
Category: Architecture
Description: The sweep that prunes the CI-published prebuilt-bundle store — where it is registered and how to prove it, why its deletion default is off in the chart and on in the code, what the "unreferenced" predicate can and cannot see, and how to read the report before arming anything.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18"/><path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6"/><path d="M14 11v6"/></svg>
---

# Prebuilt Bundle Retention

The prebuilt-bundle store is where CI's published NodeType bake lands on a portal's data volume —
`<PreWarm:PrebuiltBundleRoot>/<framework-identity>/<source>/…`, one identity directory per platform
build. It grows by one directory per CI build for ever, and on 2026-09-08 it filled
memex.systemorph.com's 16 GiB `/data` to **3 MiB free**: 482 identity directories, 13 398 MiB. A full
Azure Files share **truncates writes silently**, so every runtime recompile landed as `Bad IL format`
and the bake read-back answered `ResourceNotFound` for 39 of 45 files, with nothing reporting a fault
until somebody looked at the free space.

`PrebuiltBundleRetentionHostedService` is the sweep written in response. This page is what a reader
needs before touching it: where it is actually registered, why "it has zero callers" was measured
twice and was wrong both times, which default arms deletion, and what the protected set does **not**
cover.

## Where it is registered — and the measurement that keeps getting this wrong

The sweep is registered **once**, in the boot path both portal hosts call:

```
memex/Memex.Portal.Shared/MemexConfiguration.cs   →  ConfigureMemexMesh
    builder.ConfigureServices(services => services.AddModuleGenerationsGc(moduleRoot));
    builder.ConfigureServices(services => services.AddPrebuiltBundleRetention(configuration));
```

`MeshBuilder.ConfigureServices` runs against the **host** `IServiceCollection`
(`MeshHostApplicationBuilder` is constructed as `base(x => x.Invoke(Host.Services), address)`), so
`AddHostedService<PrebuiltBundleRetentionHostedService>()` lands on the generic host's roster and the
service does start. `Memex.Portal.Distributed/Program.cs` and `Memex.Portal.Monolith/Program.cs` (in
MeshWeaver.Plugins) both call `ConfigureMemexMesh`, and both reference
`memex/Memex.Portal.Shared` by project.

🚨 **`grep -rn AddPrebuiltBundleRetention src/ test/` returns one hit — its own declaration — and
that is not an answer.** This repository has THREE roots that ship code: `src/`, `test/` and
`memex/`. The registration lives in the third. Reported as "zero callers, never run" it produced an
issue (#3963) whose entire premise was a denominator error, and the same sweep was repeated
independently with the same restricted roots and the same conclusion. The sibling
`AddAssemblyCacheRetention` reads as *registered* on the identical search only because its
registration happens to be duplicated per host in MeshWeaver.Plugins — so using it as the "positive
control" confirms the error rather than catching it.

Two things follow, and both are now asserted rather than remembered:

| Question | Instrument |
|---|---|
| Is the sweep on the hosted-service roster? | `PrebuiltBundleRetentionIsRegisteredTest` (Memex.Portal.Shared.Test) — drives the real `ConfigureMemexMesh` and asserts an `IHostedService` descriptor whose implementation type is the sweep. Its positive control is the sibling `AddModuleGenerationsGc` registration, asserted first, so a harness that reached no registration at all fails loudly instead of passing. |
| Is deletion armed by omission? | `PrebuiltBundleRetentionArmingGuard` (MeshWeaver.Hosting.Test) — see below. |

A registration test is not optional decoration here. The service ships with a test per keep rule, a
ledger, a report-only mode and a documented discipline, and **all of it is inert on a host that never
constructs it** — with no observable difference, because "found nothing to collect" and "never ran"
both produce no deletion, no ledger line and no log. That is the shape of a CI gate that skips.

## Deletion: the code says yes, the chart says no

`PrebuiltBundleRetention.Delete` defaults to **`true`**. The chart now binds the disarming key with
an explicit `default "false"`:

```yaml
# deploy/helm/templates/memex-portal/config.yaml
PreWarm__PrebuiltBundleRetention__Delete: "{{ .Values.config.memex_portal.PreWarm__PrebuiltBundleRetention__Delete | default "false" }}"
```

Until that line existed the knob was **unreachable**. The portal ConfigMap template enumerates every
key it renders explicitly — there is no catch-all `range` over `.Values.config` — so a values file,
a deployment overlay, or a `Hosting/Deployment` record's `extraPortalConfig` setting
`PreWarm__PrebuiltBundleRetention__Delete` reached no container, silently. It is the same silent-drop
shape that left the CI-bake lane inert for months (#1660 WS3) and dropped twelve OpenRouter model
keys (#2203) — except that here the omission does not disable a feature, it **deletes**.

Measured on the live control instance 2026-09-10: both `memex` and `memex-cloud` carry
`PreWarm__PrebuiltBundleRoot=/data/prebuilt-bundles` and **no** `…Delete` key anywhere — record,
overlay, ConfigMap or inline env. `deploy/aks/values.aks.yaml` and
`deploy/homebrew/share/values.local.defaults.yaml` mount the same root, so every AKS instance and
every memex-local install was in the same state. The chart line disarms all of them at once.

**The rule is the sibling's**, stated in `AssemblyCache__Retention__Delete`'s own comment: read one
report, confirm the kept set matches your roll cadence, *then* arm it — a first run that deletes on
unexamined defaults is the one mistake here that cannot be undone. This sweep belongs to that class,
never to the armed-by-default `Notifications__Retention__Enabled` class: what it removes is bytes
something is **executing**, not a stale pointer to a node that still exists.

## What the report says, and why it names a count

Every pass writes one line to `<root>/_retention/ledger.txt` and records the result on
`PrebuiltBundleRetentionStatus`. The line leads with the **denominator**:

```
2026-09-10T02:15:00.0000000+00:00 report only — 482 identity directory(ies) / 13398.4 MB —
live=s417d365 (3.0.0-ci.8238), collectable=3 / 61.2 MB, markers to retire=3
```

That shape is the point. `collectable=0` on a store of 482 and `collectable=0` on a store the pod
cannot see are the same number and opposite facts, and a ledger that counted only *actions* would
make a sweep aimed at an unmounted share indistinguishable from a clean one. A pass that could not
scan at all writes `ABORTED before planning: …` instead, and a pass whose reference set was
incomplete writes `ABORTED: …` with the plan's summary — both are failures, never passes.
`TheReportNamesItsDenominator_SoAnUnexaminedStoreCannotReadAsACleanOne` pins the distinction.

**Reading it today is break-glass.** `PrebuiltBundleRetentionStatus` is written and has no reader:
the ledger is a file on the data volume and the log line is in Loki. Surfacing the last pass on the
Fleet Console — or in the pod's health payload beside `nodetype_bake` — is the step that makes
"read one report" something an operator can actually do, and it is not built yet.

## What "unreferenced" protects — and the gap

`PrebuiltBundleStore.Plan` keeps an identity when **any** of these holds:

1. it is the framework identity this process runs;
2. its seal could not be read (unreadable is never unreferenced);
3. a **clean** — non-pre-release — `_releases` marker names it;
4. a `PinnedPlatformReference` names it, or names a version whose marker names it;
5. a NodeType record's `CompiledFrameworkVersion` adoption stamp names it;
6. it is the newest sealed publication of a source on some represented platform major;
7. it is inside the 30-day minimum-age window (floor-clamped; a recent marker counts too);
8. a source is unsealed and the newest write is inside the unsealed grace (a seal may be in flight).

Anything unreadable, and any pin that resolves to no marker, **aborts the whole pass** with nothing
collected. The direction is right: incomplete licenses no deletion.

🚨 **But rule 4's inventory is exactly two things** — `DeploymentPinnedReferences` reads
`Hosting/Deployment` records' `pinnedImageTag`, and `Hosting/ModuleInventory` reports filed by
*registered instances*. **A satellite CI gate pinned to an older platform build is neither.** A
`MW_PLATFORM_REF` in MeshWeaver.Plugins, Reinsurance, SocialMedia, Manufacturing, Crm or Education is
a value in a GitHub Actions workflow: it files no instance report, holds no Deployment record, and
writes no adoption stamp into this mesh. Such an identity is therefore protected by rule 3 or rule 7
and by nothing else — and satellite pins are `X.Y.Z-ci.<n>` **pre-releases**, so rule 3 does not
apply. **Past 30 days, a satellite's pinned platform build is collectable.**

That is precisely the #3876 failure mode (a pruned identity directory breaking a satellite gate) and
the registry-side twin Memex#219 (*"ACR retention can delete a tag a committed overlay or a live
workload references — twice now"*). Closing it means giving the satellites a first-class reference —
a pin source that reads the fleet's `MW_PLATFORM_REF` values, or a report a satellite lane files the
way an instance does. Until then, **arming deletion on the registry (memex-cloud) is the dangerous
half**, because that is the store a satellite's gate pulls from over the HTTP prebuilt surface; the
gap must be closed, or the arming scoped to instances no CI lane pulls from.

## Rolling it out

1. **Report-only, everywhere** — the chart default above. The sweep runs, plans and writes its
   ledger line; nothing is removed. This is the state the platform ships in.
2. **Read a real report** on memex and on memex-cloud, and confirm the protected set is what it
   should be. Do not skip this by reasoning about the rules: the rules are unit-tested against a
   temp tree, while what has never been checked on a live portal is whether the *reference inventory*
   reads completely — `StampedIdentities()` throws with no mesh hub or `IMeshService`, and a
   `PinnedPlatformReferenceSource` that errors aborts the pass. Either would abort every pass,
   silently, exactly as an unregistered sweep would.
3. **Close the satellite gap**, or scope the arming away from the registry.
4. **Arm deletion per instance**, by setting `PreWarm__PrebuiltBundleRetention__Delete: "true"` in
   that instance's record — which now reaches the container, because the chart renders the key.

## Related

- [Sealed Publication Generations](../SealedPublicationGenerations) — the generation layout inside a
  retained identity, and the generation-level collection this sweep also performs
- [CI Content Bake](../CiContentBake) — what publishes into this store
- [Node Type Compilation](../NodeTypeCompilation) — the assembly-cache retention sibling on the same
  volume
