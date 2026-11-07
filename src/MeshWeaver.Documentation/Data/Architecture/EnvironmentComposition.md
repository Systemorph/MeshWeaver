---
Name: Environment Composition
Category: Architecture
Description: What each environment pre-installs — expressed as feature flags it declares per environment, reconciled on every boot — and how a package's required connection strings and endpoints are routed from the environment's service graph instead of a config key the package invented.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z"/><line x1="4" y1="22" x2="4" y2="15"/><circle cx="18" cy="18" r="3"/></svg>
---

# Environment Composition

Two portals run the same image and must not carry the same content. The public instance lists
**all of Plugins**; a working (company) instance lists **the same, without the games and fun stuff**. This
page is how that is said — once, in each environment's own values file, with no rebuild and no
hand-patching of a live cluster.

## The flag IS the composition unit

A **feature flag** is a named switch an environment turns on or off, optionally carrying the packages
that environment ALWAYS has. It is declared under `Features:Flags:{name}` — the same `Features`
section an operator already edits per environment (see [Feature Flags](/Doc/Architecture/FeatureFlags)
for the fixed capability toggles that live beside it).

```jsonc
"Features": {
  "Flags": {
    "plugins":  { "Packages": ["Plugins/*"], "Description": "The whole platform plugin repo." },
    "games":    { "Packages": ["Plugins/Chess", "Plugins/ThreeBody"] },
    "betaChat": true
  }
}
```

Environment-variable form, which is what actually reaches a pod:

```
Features__Flags__plugins__Packages__0=Plugins/*
Features__Flags__games__Enabled=false
Features__Flags__betaChat=true
```

| Rule | |
|---|---|
| **Declared ⇒ on** | Declaring the flag in an environment's values IS the opt-in. `Enabled: false` switches it off *without deleting the declaration*, which is what makes a shared base file plus a one-line per-environment override work under helm's last-wins layering. |
| **Undeclared ⇒ off** | `IsEnabled` on a flag nobody declared is `false`. |
| **An ENABLED flag installs its packages** | Reconciled on **every** boot. |
| **A declared-but-DISABLED flag EXCLUDES its packages** | And the exclusion **wins over every other selection signal**, the platform's own `preInstalled` baseline included. |
| **A non-boolean `Enabled` is not consent** | `yes` / `1` / `on` read as OFF and are named at Warning. These flags install content. |
| Flag names are matched **case-insensitively** | Configuration keys are, and env-var delivery mangles casing. |

Packages are named in the `Source/Package` notation the plugin grants and
`PluginCatalog:InstallByDefault` already use (`Plugins/*`, `Reinsurance/UWDeepfield`), matched by the
same source-scoped `PluginGrantEntry`. There is no second matching rule — and no package name appears
anywhere in platform code.

### The two portals, in full

One shared declaration; the environment that does not want the games flips a single key. 🚨 Read
the namespace off the instance's own record, never off its host name — the two need not match:

| instance | its values file adds |
|---|---|
| the public instance | *(nothing — both flags declared, both on)* |
| a working instance | `features.games.enabled: false` |

```yaml
# shared
features:
  plugins:
    description: "The whole platform plugin repo."
    packages: ["Plugins/*"]
  games:
    description: "Games and demos."
    packages: ["Plugins/Chess", "Plugins/DoublePendulum", "Plugins/FractalStars", "Plugins/ThreeBody"]

# values.<working-instance>.yaml — the ONE line that differs
features:
  games:
    enabled: false
```

Whether a borderline package (RolePlay — arguably serious training) belongs to `games` is a values
decision, made where the flag is declared. Nothing in the platform knows any package's name.

### Allow-list or exclusion? Both, and neither is silent

They fail differently, and the difference matters:

- an **allow-list** silently **omits** a package newly added to a repo — it never reaches the portal
  and nobody notices;
- an **exclusion** silently **includes** one.

So a flag expresses whichever the operator means, and the ambiguity is removed by making both
visible: the **Composition** admin tab names exactly which flag decided each package, the boot log
names the composition it installed, and an exclusion naming a source this installation does not have
is reported at **Error** — that one fails *open* (the packages it meant to keep out are installed),
which is the more dangerous direction.

## Three lanes, and why they are different knobs

The boot pass (`InstanceAutoRegistrationService`) folds three selection signals into one ordered
install:

| lane | declared by | when | why |
|---|---|---|---|
| `preInstalled` on the package manifest | the **package author** | **every boot** | the platform's own baseline (the Agents and Skills libraries, Essentials). Suppressed by `PluginCatalog:InstallPreInstalledPackages=false` — and, with that key unset, by an `InstallByDefault` that names an explicit set (`Source/Package` entries only): a deployment that names its packages gets those and their `requires`. It is what heals an instance whose baseline partition was lost. |
| `defaultApp` on the package manifest | the **package author** | **every boot** | the narrow **default-apps lane** (Threads, Examples): an app every instance carries. 🚨 The one exception to the exact-set rule above — it is **kept beside an explicit `InstallByDefault` set** and suppressed only by an explicit `PluginCatalog:InstallPreInstalledPackages=false` (one switch for "this instance manages its own content", not two). Free by contract; pair it with `preInstalled` and `tier: free`. |
| **`Features:Flags:{name}:Packages`** | the **environment** | **every boot** | "this environment always has X". |
| `PluginCatalog:InstallByDefault` | the operator | every boot, **once per package** (ledger-gated) | seeds what it has never delivered, except a package newly listed under a whole-source pattern on an already-seeded instance, which is HELD for a governed `package.provision` (no ledger entry); a commercial package that passes the hold is recorded as `skipped`, never installed. |

🚨 **`InstallByDefault` cannot express a per-environment policy, and that is by design, not an
oversight.** It seeds — the ledger records what it has delivered and it never re-asserts — so an
admin who later uninstalls a package is not fought by the next restart. The consequence is that on an
already-populated portal it re-asserts nothing: it can add a package it has never delivered, but it
cannot keep one present. A composition policy wants
the opposite, so it is a **separate** lane with **reconciled** semantics; the seed's meaning is
untouched, and the two coexist.

Reconciling costs an up-to-date instance nothing: the content-identity gate in
`CatalogLayoutAreas.InstallOrUpdate` turns the pass into one catalog listing and no writes.

### A disabled flag retires what is installed

A flag declared off **in so many words** — `Enabled` present and the boolean `false` — also
**retires an installed copy** of every package it names, at boot, through **phase 1** of the
[package uninstall request](../PackageUninstall): the module is retired, the install record is
removed, and **no data is dropped**. That is how an estate turns a pre-installed package (MCP, a
harness) off through its record alone, with no call to the endpoint it wants gone
(MeshWeaver.Plugins#5055). The record is the source of truth: under policy `declared-not-derived`
pruning removes what the declaration dropped, and an explicit `false` is such a declaration.

| the flag | install selection | an installed copy |
|---|---|---|
| declared, `Enabled` absent or `true` | included | kept |
| declared, `Enabled: false` | excluded | **retired** (phase 1) |
| declared, `Enabled` not a boolean (`no`, `0`, `off`) | excluded, named at Warning | kept — an unreadable value is not a declaration |
| **not declared at all** | — | kept — an absent contributor is never a removal |

How it runs (`DeclaredDisabledRetirement`, called by the boot pass before the install selection):

- **Only the declaration counts.** It reads `FeatureComposition.Retired` — the patterns of flags whose
  `FeatureFlag.DeclaredDisabled` is set — matched against the install records with the same
  source-scoped `PluginGrantEntry` as every other lane. A record that names no source matches no
  source-scoped pattern and is **not** retired; it is named at Warning.
- **A package another installed package requires is REFUSED, by name.** Retiring `Mcp` while
  `Essentials` (which requires it) stays installed would leave `Essentials` failing at use, so the
  retire is refused and logged at **Error** naming the flag, the package and every dependent — an
  Error the instance's error telemetry carries as a signature, on every boot until it is resolved.
  Declaring the dependents off as well retires them together. The refusal propagates: a refused
  package stays installed, so what *it* requires is refused too.
- **One request per install, on every replica.** Every replica's boot runs the retire. The request id
  is derived from the install record (package, `installedAtUtc`, version —
  `DeclaredDisabledRetirement.RequestIdFor`), so replicas booting together collide on ONE
  `Admin/_PackageUninstall/flag-retire-…` node; the loser reads it. A later boot finds the record
  gone and decides nothing, so the pass is idempotent. A request that already exists is read and
  reported — a refused one (`Failed`) at Error, every boot.
- **Every replica drops the module.** The request carries `RetireOnEveryReplica`: after disabling the
  landed generation (on the volume all replicas share), phase 1 requests **one restart** even when
  it retired the module live on its own process, so no sibling replica keeps the module — and the
  routes it mapped, such as `/mcp` — until some later restart. Where no restart path exists the
  request says so, and siblings drop it at their next restart.
- **Never phase 2.** The request names no requester, and phase 2 refuses a confirmation for such a
  request — a flag can never drop a partition. Phase 1's own refusals (a shared partition, user data
  in the partition, an unreadable node) still apply and end the request `Failed`, by name.
- **The boot repair never re-writes it.** `InstalledPackageRepairService` lists the install records
  before it waits for the default install, so the record it would re-assert can belong to a package
  phase 1 is removing. `ReassertInstalled` therefore reads the same composition and skips every
  package the retire's own rule covers (`DeclaredDisabledRetirement.CoveredBy` — the one decision
  `Decide` uses too), each skip reported on the summary's `Skipped`.
- **It blocks no re-install.** A flag-issued request (`RetiredByFlag`) is not counted by
  `PackageUninstallExecutor.UninstalledHere`: while the flag stays off its exclusion keeps the package
  out, and once the record stops declaring it off the package's own lanes bring it back.

**An exclusion is applied last, after the dependency closure**, so it also removes a package the
closure pulled back in as somebody's requirement — the operator's explicit statement outranks an
inferred edge. That case is named at Warning, because whatever required it will now fail at use.

## Reading a flag: `IFeatureFlags`, reactive

```csharp
var flags = hub.ServiceProvider.GetRequiredService<IFeatureFlags>();

flags.IsEnabled("betaChat").Subscribe(on => …);   // re-emits when configuration reloads
flags.All.Select(all => …);                       // every declared flag, for a view to bind
flags.Composition.Subscribe(c => …);              // what this environment includes / excludes
```

🚨 **There is deliberately no synchronous `bool IsEnabled(string)`.** Configuration is layered and
reloadable (`MemexConfiguration` opens its JSON with `reloadOnChange: true`), so a sampled value is
stale the moment a provider reloads — and a synchronous reader is *indistinguishable from a correct
one* at the moment it is first called. `ConfigurationFeatureFlags` is a mesh-scoped singleton holding
its state on an instance field, re-read by a `ChangeToken.OnChange` **push** from the configuration
provider: no timer, no poller, no watchdog.

## Package parameters — routed from the environment's service graph

A package declares what it needs; the **environment** decides where it comes from. That is the
difference between a package naming a *service* and a package inventing a *config key* — the live
counter-example being the Cosmos storage backend, which documents a `ConnectionStrings:memexcosmos`
convention that nothing reads, leaving `Graph:Storage:ConnectionString` as its only real channel.

Declared on the package root's own content, beside `preInstalled` and `module`:

```jsonc
"parameters": [
  { "name": "warehouse", "kind": "ConnectionString",
    "description": "The Snowflake warehouse this pack reads." },
  { "name": "crm",    "kind": "Endpoint" },
  { "name": "apiKey", "kind": "Value", "optional": true }
]
```

| kind | resolves from | who injects it |
|---|---|---|
| `ConnectionString` | `ConnectionStrings:{name}` | Aspire `WithReference(db)` → `ConnectionStrings__memex`; on AKS the chart secret or the Key Vault CSI mount |
| `Endpoint` | `Services:{name}:{https\|http\|default}:0` | Aspire `WithReference(project)` → `services__{name}__https__0` — the `Microsoft.Extensions.ServiceDiscovery` shape, already registered in `Memex.Portal.ServiceDefaults` |
| `Value` | `Parameters:{name}` | an Aspire `AddParameter`, or a plain env var |

`Service` overrides `Name` when a package's own vocabulary differs from the deployment's. `Optional`
defaults to **false — required**, so the gate is closed for anything an author did not deliberately
open (and the CLR-default `false` round-trips loss-free under the default-suppressing serializer).

### It fails closed, and it names what to provision

The gate sits on `CatalogLayoutAreas.InstallOrUpdate`, the single orchestrator every install lane
funnels through — the boot default install, the Store's Provision click, the auto-update reconciler —
beside the entitlement gate. A missing required parameter faults the install:

```
Package 'finance-pack' requires 1 parameter(s) this environment does not supply:
  warehouse (ConnectionString) — The Snowflake warehouse this pack reads.
    provision: ConnectionStrings__warehouse
Nothing was installed.
```

The unattended lane counts it **`Failed`** in the `DefaultInstallSummary` and logs at Error. It is
never installed half-configured (content that errors at first use with nothing pointing back at the
missing key) and never *silently skipped* — a skip that reads as success makes "the gate never ran"
and "the gate passed" indistinguishable, which is the trapdoor shape AGENTS.md forbids in gates. A
**blank** value is not a supplied one: the chart renders empty strings for unset keys.

## Where the configuration actually comes from

Per-environment configuration reaches an AKS pod as **environment variables only**: nothing sets
`ASPNETCORE_ENVIRONMENT`, so all three portals run as `Production` and load byte-identical
appsettings. The values files live **outside this repo** (`deploy/aks/envs/.gitignore`), in the
private deployment repo.

🚨 **The chart's ConfigMap enumerates every key by hand — an un-templated key is silently dropped.**
`deploy/helm/templates/memex-portal/config.yaml` renders `Features__Flags__*` from a `features:` map
in values, and hand-patching a live deployment does **not** stick: the next `helm upgrade` reverts
it. Composition is therefore expressed in the values file, and the **Composition** admin tab is
deliberately read-only.

## Seeing it: the Composition tab

**Admin app (`/Admin`) → Composition** (platform admins only) shows two tables:

- **Feature flags** — every declared flag, whether it is on, whether it installs or excludes, its
  packages and its purpose. Bound to `IFeatureFlags.All`, so it is never a startup snapshot.
- **Required parameters** — every parameter the installed packages declare, the exact env var to
  provision it, and whether this environment supplies it.

## Related

- [Feature Flags](/Doc/Architecture/FeatureFlags) — the fixed `Features` capability toggles beside these.
- [Plugin Registry](/Doc/Architecture/PluginRegistry) — grants, `InstallByDefault`, and the default install.
- [Plugins](/Doc/Architecture/Plugins) · [Plugin Packaging](/Doc/Architecture/PluginPackaging) — what a package is.
- [Modules](/Doc/Architecture/Modules) — the compiled-assembly lane, activated by `Modules:Assemblies`.
- [Deployment](/Doc/Architecture/Deployment) — how a values file becomes a running portal.
