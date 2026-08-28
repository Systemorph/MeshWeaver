---
name: /driver-release
nodeType: Skill
displayName: Driver release — how a compiled module reaches an instance
description: Ship or diagnose a compiled MeshWeaver module (a driver — AI provider, MCP, mail, notifications, observability, indexing, export, self-update). Use when a package declares content.module, when a release removes drivers from the image, or when a mesh cannot activate a package's nodes.
icon: 🔌
category: Engineering
order: 45
---

# Driver release — how a compiled module reaches an instance

A **driver** is a package that ships compiled C# — its root declares `content.module` (the entry
assembly, e.g. `MeshWeaver.AI.OpenAI`). That is different from an ordinary node-native plugin,
whose `Source/` the mesh compiles itself with Roslyn. A driver's bytes are built by CI and must
physically arrive on the instance.

**The fourteen drivers in this repo** — `AzureFoundry`, `ClaudeCode`, `Copilot`, `OpenAI`,
`WebSearch`, `Mcp`, `Mail`, `Teams`, `Notifications`, `Observability`, `Indexing`, `Export`,
`Hosting`, `Edu`. Regenerate the list, never retype it:

```bash
python3 -c "
import json,glob
for f in sorted(glob.glob('*/index.json')):
    c=(json.load(open(f)).get('content') or {})
    if c.get('module'): print(f.split('/')[0], '->', c['module'], c.get('minMeshVersion'))
"
```

## Drivers are platform-administered, never sold

Every driver root carries **`preInstalled: true`** and **`price: null`**. That flag is the
manifest's ONE statement of *"this ships with the platform"*, and it changes three things at once:

- the Store never sells it — there is no price, no entitlement, no per-viewer funnel;
- its content is **not** gated — `PluginGate`'s reconcile retracts a pre-installed package's child
  denies, so it reads like platform surface rather than a purchase;
- **`InstanceAutoRegistrationService` installs it at boot**, from the instance's configured
  registries. `PluginCatalogOptions.InstallPreInstalledPackages` defaults to `true`.

So the global-admin action is *provisioning the package on the registry* and issuing the grants —
not clicking Get on fourteen cards. A driver a viewer could buy would be a bug.

## The two places a module's bytes can live — and the precedence

| location | written by | when |
|---|---|---|
| `<app>/modules/<Name>/` | the **image build** (`MeshModulesPublish.targets`, the `MeshModuleClosure` lane) | at docker build time |
| `<ModuleRoot>/modules/<Name>/` | **`ModuleLandingService`**, from a registry publish | at run time |

`MeshBuilder.ResolveModulePath(entry, moduleRoot)` probes **landed → image → app closure**. A
landed module is the one an operator just published, so it wins over a stale baseline of the same
name. During a transition BOTH exist, and that overlap is what makes a zero-downtime driver
migration possible (below).

## 🚨 The module root must be writable AND shared

`ModuleRoot` (config key **`Modules:Root`**) is resolved once and used by the writer and by boot
activation, because a landed module read from a different directory is simply invisible. It
defaults to `AppContext.BaseDirectory`.

**That default cannot be used on a deployed portal**, for two independent reasons:

1. `/app` is the **read-only** image layer. The image WRITES `modules/` at build time; the runtime
   only reads it — so the defect is invisible until the first thing that must write it, which is
   the registry's publish route. On 2026-08-19 that failed all fourteen bundles with
   `Access to the path '/app/modules/.staging-…' is denied`, surfaced to the build as **HTTP 409**,
   four steps from the cause.
2. Even where it is writable, `/app` is **per-pod**. A module landed by whichever replica served
   the publish must be visible to every other replica, so the root has to be the deployment's
   shared volume — on AKS the RWX `/data` PVC every portal pod already mounts.

Set `Modules:Root=/data` on any deployment that lands modules.

## Shipping a driver

```
build  →  bundle  →  publish to the registry  →  land  →  restart  →  provision the package
```

1. **CI packs** the module (`node-repo-module-pack.yml`) into a `.module.nupkg` carrying the entry
   assembly, its private closure, `minMeshVersion` and the framework MVID.
2. **Publish** it: `POST /api/plugins/bundles/{plugin}` with the publish token
   (`Plugins:Registry:PublishToken`). 🚨 When that key is unset the route is **not mapped at all** —
   an unconfigured registry has no publish surface rather than one answering 401.
3. **Land**: the registry writes `<ModuleRoot>/modules/<Name>/` and appends the
   `modules/activation.json` entry with `PendingRestart = true`.
4. **Restart-as-activation** — nothing is loaded into the running process. The next boot folds the
   sidecar's enabled entries into `Modules:Assemblies` and each pack's `MeshNodeProviderAttribute`
   registers.
5. **Provision** the package partition so its NODES exist too (`Store/Provision`, System identity).

## 🚨 Never roll a driver-removing release before the drivers have landed

A release that deletes drivers from the image (MeshWeaver #1882 removed fourteen) means the
instance must get them from the registry instead. But landing requires a writable `Modules:Root`,
which only exists in an image carrying that support — so the order is forced:

1. roll onto an image that has the writable root **and still ships the drivers**;
2. set `Modules:Root`, publish the bundles, let them land, restart;
3. provision the packages and **verify** (chat, MCP, mail — whatever those drivers serve);
4. only then merge the removal and roll the release without them.

Doing it the other way leaves a window with no chat, no MCP, no mail, no notifications, no
observability. **A missing driver does not error — it HANGS**: a node whose module is absent cannot
activate, so every read costs the caller the full 60s `SubscribeRequest` timeout and the page dies
with nothing saying "missing module".

## Retiring a driver's NuGet package

A module that leaves the platform stops being packed, but everything it already published stays on
nuget.org **listed** — in search and in `dotnet add package`, offered as current while nothing
builds or patches it. Retire it:

```bash
python3 scripts/orphaned-nuget-packages.py --root .          # report (MeshWeaver repo)
```

The set is DERIVED — *(published under the MeshWeaver prefix) minus (what this tree still packs)* —
so it needs no editing as further modules move out. Apply it through the
**Unlist orphaned NuGet packages** workflow (`apply` + `confirm: UNLIST`).

- ⚠️ **Check the scope before applying.** A package that left core might still be published by a
  satellite. Today none are: the satellites' publish workflows are `dry-run: true` and target
  GitHub Packages, so **nuget.org is fed only by core's tag-triggered `release-packages.yml`**.
- `dotnet nuget delete` **unlists**; it does not erase. Existing pins keep resolving by exact
  version and a listing can be restored from the UI — that reversibility is what makes automating
  it safe.

## Diagnosing

| symptom | look at |
|---|---|
| publish returns **409** `Access to the path … is denied` | `Modules:Root` is unset or not writable |
| publish returns **401** | the publish token; a *missing* token unmaps the route entirely (404) |
| package provisions but pages hang ~60s | its module never landed, or landed and the pod never restarted |
| the Store card says *available from package source* with a **Provision** button | the package is not on this mesh at all — that is accurate, not a display bug |
| `get @{Package}` → *Not found* | authoritative. An HTTP 200 on `/{Package}` proves nothing: the SPA routes client-side |

Read the live state, never infer it: `get @{Package}` through the mesh MCP, and the landed set from
`<ModuleRoot>/modules/activation.json`.
