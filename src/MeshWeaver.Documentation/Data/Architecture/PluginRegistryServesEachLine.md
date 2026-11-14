---
Name: The Plugin Registry Serves Each Line
Category: Architecture
Description: How an instance decides which ref of a plugin repository it lists, installs and syncs, and how a registry source declares one ref per platform line (lineRefs) so a v3 instance is served main3 while a v4 instance is served main.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="6" y1="3" x2="6" y2="21"/><line x1="18" y1="3" x2="18" y2="21"/><circle cx="6" cy="8" r="2"/><circle cx="18" cy="16" r="2"/></svg>
---

# The Plugin Registry Serves Each Line

Policy `major-lines-run-separately` (the design page *Major Lines Run Separately*, §4b) runs the v3
and v4 platform lines side by side: core and every plugin repository keep `main3` for v3 and `main`
for v4. The plugin registry has to follow: an instance whose record says `updatePattern: "3.*"` must be
listed and served each repository at its v3 ref, and an instance on `"4.*"` at `main`. A v3 instance
must never be offered a v4-only package version or v4 source.

This page records how the ref was chosen before lines existed, and the declaration that now chooses it.

## How the ref was chosen before lines

- **The record names ONE ref per git source.** A registry record lists its git sources as
  `pluginRepos[]` with `isRegistrySource: true` and a `ref` (`PluginRepoMount.Ref`, blank means `main`,
  `PluginRepoMount.EffectiveRef`). It renders as `PluginCatalog:Sources:N:Ref`
  (`DeploymentPortalConfig.ConfigurationEntries` on the boot and Aspire routes; the chart's
  `PluginCatalog__Sources__N__Ref` on the Helm route, MeshWeaver.Deploy `templates/memex-portal/config.yaml`).
- **The registry serves that ref to every caller.** `PackageSources.FromConfiguration` is the one
  reader of the sources (the registry endpoints, the default install, discovery and the update watcher
  all call it), and the `/api/plugins` listing and `/api/plugins/files` routes (MeshWeaver.Plugins
  `PluginRegistryEndpoints`) read `ConfiguredPackageSource.GitRef`. The consumer's own ref
  (`PluginCatalog:Registries:N:Ref`, default `HEAD`, sent as `?ref=`) is ADVISORY and ignored, except a
  full commit sha reachable from the configured ref (the snapshot token, `ReadRef`).
- **A consumer mount carries no ref.** `PluginRepoMount.Ref` is documented "ignored by a consumer
  mount", and the chart renders the consumer's `Registries:N:Ref` as `HEAD`.
- **The bundles are already per line.** Prebuilt assemblies are fetched per framework identity
  (`PluginBundleClient` sends `?identity=` the running platform's live identity), and adoption accepts
  only a bundle of the running major (`PrebuiltAdoptionPolicy`, `VersionStrictness.Family`). The
  registry's bundle store is keyed by identity, not by git ref, so a v3 instance already receives only
  bundles built for its own compatibility key — the ref question is about the SOURCE files and the
  package LISTING alone.

So once MeshWeaver.Plugins `main` carries v4, every instance — v3 included — would be listed and served
v4 source.

## The declaration: `lineRefs` on a registry source

A registry source declares the ref it is served at per line, on the record:

```json
{
  "name": "Plugins",
  "url": "https://github.com/Systemorph/MeshWeaver.Plugins",
  "ref": "main",
  "lineRefs": { "3": "main3" },
  "isRegistrySource": true
}
```

- `ref` stays the ref for every line `lineRefs` does not name (`PluginRepoMount.RefForLine`).
- It is declared per repository, never derived from a branch-naming convention: a repository that has
  no `main3` yet is simply not given one, and keeps being served at `ref`.
- It renders NAME-keyed — `PluginCatalog:LineRefs:{source}:{major}={ref}` — on the boot and Aspire
  routes (`ConfigurationEntries`) and through the Helm portal config's pass-through
  (`DeploymentPortalConfig.PortalConfig`), so neither route depends on the index the chart gives a
  source. A record that declares none renders exactly what it rendered before.
- `Validate` refuses it on a consumer mount, for a line that is not a number, for a blank ref, and for
  two sources whose names key alike.

## Who states which line

- **A consumer states its line on every request.** `RegistryPackageSource.Line` is read once from the
  instance's configuration (`PlatformLine.Of`): the explicit `PluginCatalog:Line` when set, else the
  major of the record's `updatePattern` (`SelfUpdate:DefaultPattern`, `3.*` → 3), else the major of the
  running platform version. It rides as `&line=3` on the listing and `"line":"3"` in the files body.
  When no line is known the request is byte-identical to the one sent before lines existed.
- **The registry serves each source at the caller's line.** `ConfiguredPackageSource.ForLine` moves
  `GitRef` to the declared ref for that line, or back to the configured ref for a line nobody declared.
  The listing and the files route apply the SAME line, so a caller is never listed one line's package
  and served another line's files for it.
- **A caller that states no line** runs an image older than the line-stating client. It is served
  `PluginCatalog:UnstatedCallerLine` when the registry declares one, else the registry's OWN line.
- **In-process readers use the instance's own line.** `FromConfiguration` applies the instance's own
  line, so on the registry itself the default install, module discovery and the update watcher read
  the repository at the ref of the line the registry runs.

## Landing order

1. Core: the record field, its rendering and the `PlatformLine`/`ForLine` reader. Changes nothing for a
   record without `lineRefs`.
2. MeshWeaver.Plugins: the registry endpoints serve per line. It needs a platform floor at the first
   sealed core set carrying step 1 (policy `request-min-version-wait-until-deployed`).
3. The Memex records declare `lineRefs`, once the control instance runs a build that reads the field
   and the registry runs step 2. Only repositories that already have their `main3` get an entry.

Until step 3 nothing in the fleet behaves differently.

## What this does not cover

- Which commit a sealed bake publishes for a source is decided by the seal machinery
  ([The Sync-Ref Contract](../SyncRefContract)). Once `main3` and `main` diverge, the v3 bundles must come
  from the v3 line's own build, as the design page's §4 says. This page does not change that path.
- A consumer reads each registry through one mount, so the line is per INSTANCE, not per repository. A
  v3 instance that wants one v4 package is not a supported shape.
