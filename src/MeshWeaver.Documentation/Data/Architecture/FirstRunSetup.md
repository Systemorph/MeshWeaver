---
Name: First-Run Setup
Category: Architecture
Description: An image with no database boots into a setup wizard that asks for the database, the sign-in routes, the model keys and the modules — what it writes, where the secrets live, and why it is a separate host
Icon: Server
---

# First-Run Setup

**Status: delivered. This is slice 3 of [Instance Identity and Setup](../InstanceIdentityAndSetup) —
the setup surface. Slices 1 and 2 (the licence on the instance, memex-cloud as a record) shipped
earlier.**

An instance with **no `Graph:Storage` configuration and no completed `instance.json`** serves a
first-run wizard and nothing else. The operator answers four questions — database, sign-in, models,
modules — the answers are written to the instance manifest, and the process stops so its supervisor
restarts it into a configured mesh.

Every deployment that exists today states its storage in configuration and never sees any of this.

## The questions, and where each answer lands

| Step | Asks | Default | Written to | Read at the next boot as |
|---|---|---|---|---|
| **Database** | Which backend, plus a connection string or directory | **SQLite** — a single file, nothing else to run | `InstanceManifest.Storage` | `Graph:Storage:Type` / `:ConnectionString` / `:BasePath` |
| **Sign-in** | The developer login and its platform admins; client id / tenant / secret per external provider | Developer login on | `InstanceManifest.SignIn` | `Authentication:EnableDevLogin`, `Authentication:DevAdminUsers`, `{section}:ClientId` / `:TenantId` / `:ClientSecret` |
| **Models** | An API key per provider, plus the embeddings endpoint | none | `InstanceManifest.Ai` | `{section}:ApiKey` / `:Endpoint` / `:Models:{i}`, `Embedding:*` |
| **Modules** | Which shipped modules boot; which packages to provision | gRPC on, `Plugins/*` | `InstanceManifest.BootModules` / `ProvisionPackages` | `Modules:Assemblies`, the store install lane |

`InstanceManifestProjection` is the single place that knows those key names, and
`AddInstanceManifest` inserts it into the host's configuration **at index 0** — so an appsettings
value, a ConfigMap key or an environment variable outranks the manifest every time. That is the
"configuration wins" rule of [`InstanceManifest`](../DataAccessPatterns) obtained structurally
rather than by a precedence check each new section would have to repeat.

## 🚨 Where the secrets live

Three different homes, because the three secrets are needed at three different moments.

| Secret | Home | Protection | Why not somewhere else |
|---|---|---|---|
| **Key-protection master key** | `Ai__KeyProtection__MasterKey` from the deployment, else `instance.key` beside the manifest | file mode `0600` | **Never in the manifest.** A manifest gets copied to a new volume, backed up, diffed and pasted into an issue when an instance will not boot; a key stored beside the ciphertext it unlocks turns "encrypted at rest" into a spelling of "plaintext". On Kubernetes the key comes from a Secret and only ciphertext is on the PVC — a real separation. On a laptop both are on one disk, which is honest and no worse than any local credential file. |
| **Sign-in client secrets** | `instance.json` → `signIn.providers[].clientSecret` | `enc:v1:` AES-256-GCM | **Cannot be a mesh node.** Authentication schemes are registered once while the host is being built — before any storage is open and before any user is authenticated. A credential the handler needs at `AddAuthentication` time cannot be read from a database that does not exist yet. |
| **Model API keys** | `instance.json` → `ai.providers[].apiKey`, as a **bootstrap envelope** | `enc:v1:` | The durable home is a `Provider/{Name}` mesh node ([Model Providers](../ModelProviders)). At setup time there is no mesh to put one in, so the manifest carries the key until the built-in provider — a sync source — imports the catalog into the `Provider` partition on the first configured boot. |
| **Database connection string** | `instance.json` → `storage.connectionString`, or `storage.secretName` naming a deploy secret | file mode, or the deployment's secret store | It is the one credential needed *to open* the store everything else lives in, so it cannot live in the store. |

Two rules follow, and both are enforced rather than documented:

- **A secret that cannot be encrypted is REFUSED, never stored in the clear.** `ProviderKeyProtector.Protect`
  throws without a master key, so the setup surface provisions one (`InstanceMasterKey.EnsureCreated`)
  *before* it collects anything. If it cannot — a read-only data directory — the form refuses and
  names both ways out. This is the 2026-08-24 leak's shape, where an unconfigured deployment
  persisted a live provider key in cleartext with nothing failing and nothing logged.
- **Ciphertext is never projected as a value.** A manifest secret that cannot be decrypted (no
  master key, or a rotated one) is DROPPED from the configuration rather than passed through.
  A handler handed ciphertext as its client secret registers, renders its button, and fails at the
  token exchange with an error naming the endpoint — the failure landing furthest from its cause.

## 🚨 Why it is a separate host, not a branch

`SetupOnlyHost.TryRun` is called first in `Program.cs` and, in setup mode, never returns. It builds
its own `WebApplication`: the setup endpoints, a health probe, and nothing else. No mesh, no
modules, no Blazor.

That shape was not chosen for tidiness. Two earlier attempts served the wizard from inside the
ordinary pipeline and **both died before reaching it**:

1. `MapMeshWeaver` asserts that a permission evaluator is registered — and on a setup-mode host
   `ConfigureMemexMesh` returned before `AddRowLevelSecurity` ever ran. The process exited with a
   security assertion.
2. Past that, `EventSubscriptionRunner` — an `IHostedService` a module registers during
   `InstallAssemblies`, which happens *before* the storage decision — could not resolve
   `IMeshService`, and the host failed to start.

Both are one fact seen twice: a portal's startup assumes a configured mesh from very early on. Every
fix that keeps the ordinary pipeline is whack-a-mole against every service any module might
register. Not building the mesh host at all has no such tail — and it is what the
`MeshBuilder.IsAwaitingSetup` doc comment always demanded: *"a host that reads this true must serve
the SETUP surface and nothing else."*

## 🚨 The setup token

The surface is **unauthenticated by construction** — it runs before there is a user store — and what
it collects is a connection string, provider API keys and the list of ids that become platform
administrators. An open form offering that to whoever reaches the port first is not a theoretical
exposure: a fresh instance is reachable the moment its ingress resolves.

So the instance mints a token per process and writes it to its own log. Whoever can read the
instance's output — and only they — can configure it, which is the same proof-of-access every local
notebook server uses. It is deliberately **not persisted**: a restart mints a new one, because the
token's whole meaning is "you can see this instance's console right now".

`memex-local setup` reads it back out of the pod log and opens the browser for you.

## What the wizard may offer is DISCOVERED

- **Storage** — the keyed `IStorageAdapterFactory` registrations this image ships
  (`StorageBackendCatalog.Discover`, read off the `IServiceCollection`, because an
  `IServiceProvider` can resolve a keyed service you name but cannot enumerate the keys).
- **Sign-in** — `SignInProviderCatalog`, which is the image's own list of routes it can serve.
- **Models** — the configuration sections the first-party provider packages bind.

A menu somebody typed would let an operator record a backend the image cannot open — failing at the
*next* boot with `Unknown storage type`, after the wizard is gone — or a provider whose handler was
never registered, where `/auth/login?provider=X` answers `400 Unknown provider` with every value
correct.

**SQLite is the pre-selected default**, and this change is what made it selectable at all: the
adapter, its partition provider, the durable event log and the local vector query had all shipped,
but no keyed factory existed, so `Graph:Storage:Type=Sqlite` answered `Unknown storage type`.

> ⚠️ **SQLite's vector search is a brute-force cosine scan**, not an ANN index — `SqliteVectorMeshQuery`
> reads every stored embedding and ranks in memory, which is right for a single machine and is not
> Postgres's HNSW. It also only lights up when an embedder is wired: without one,
> `SqliteStorageAdapter` writes `embedding = NULL`, the vector provider contributes nothing, and
> search degrades from meaning to words **with no error and no log line**. That is why the wizard
> asks for an embeddings endpoint and warns when it is left blank.

## 🚨 What a real cluster found that no test could

The wizard was written, unit-tested, and driven end to end in a browser against a local host — and
it still could not be reached on Kubernetes, for **three** independent reasons. Every one of them is
total and silent: the portal serves the wizard perfectly while nobody can get to it. They are
recorded here because each was invisible until the thing was actually deployed.

| What was wrong | Why nothing caught it | Guarded by |
|---|---|---|
| **The image pre-answered the storage question.** `Memex.Portal.Distributed/appsettings.json` baked `Graph:Storage:Type = "PostgreSql"`, so `MarkAwaitingSetup` was never reached — the chart correctly omitted the key, the pod's environment carried none, and the portal booted configured anyway. | Every test supplied configuration explicitly. Nothing asked what the *image* says when you supply nothing. | `DeployedImageDoesNotPreAnswerStorageTest` (Plugins) |
| **The pod never went READY.** The chart probes `/health` (startup) and `/alive` (readiness + liveness); the setup host mapped only `/healthz`. Every probe 404-ed, so the previous replica kept the traffic and the wizard was unreachable through the ingress. | The surface tests drive it over an in-process pipeline, where there are no probes and no replicas. | `SetupProbeEndpointsTest`, which reads the paths **out of the chart** |
| **The setup token was swallowed.** It is the only way into an unauthenticated surface that collects a connection string, model keys and the platform-admin list — and it was logged at `Information` under a category every deployment filters to `Warning`. Wizard serving, token minted, nobody able to learn it. | Tests read the token from DI, never from the log. Log *levels* are deployment configuration, invisible in-process. | It goes to **stdout**, which no log level can suppress |

The shared lesson: **a surface that must work when nothing else does cannot depend on anything
configurable.** Not a log level, not a route another component maps, not a default someone else
supplies.

## 🚨 The dangerous direction — and why it is guarded on two sides

Removing the image's baked storage default is what makes the wizard reachable. Its safety rests
**entirely** on the deployment paths supplying the value themselves. If one of them ever stops, the
failure is not a crash:

> A configured production portal with real data reboots into a **first-run setup wizard** — serving
> no content, and offering whoever arrives the chance to configure it. No exception, no crashloop,
> because "no storage configured" is now a *legitimate* state by design.

That is strictly worse than an outage, which at least looks like one. So the two halves are asserted
on opposite sides, and neither can be satisfied by the other moving:

- **The image must NOT answer** — `DeployedImageDoesNotPreAnswerStorageTest` (MeshWeaver.Plugins).
- **The deployment paths MUST** — `DeploymentPathsSupplyStorageTest` (core), covering the helm chart
  *and* `MemexHostingExtensions` (the Azure Container Apps lane, which does not use the chart at all,
  so the chart assertion says nothing about it).

The one gap no guard can see is an environment values file in the private deployment repo that
overrides the key to `""` — an empty value is omitted by the ConfigMap template, which reads
identically to never stating it. Verified by hand on the shared `memex` portal
(2026-09-03: `memex-portal-config.data.Graph__Storage__Type = "PostgreSql"`); any new environment
should be checked the same way once, at ramp-up.

## The chart had to stop answering these questions

The portal ConfigMap lists keys explicitly, so an unconditional line emits `Graph__Storage__Type=""`
for a values file that states nothing — and **an empty section is not an absent one**:
`GetSection("Graph:Storage").Get<GraphStorageConfig>()` answers a non-null object whose `Type`
defaults to `FileSystem`. The instance would boot past setup onto container-ephemeral disk.

The keys the wizard owns are now emitted only when the deployment states them. Every existing
deployment sets them in `values.yaml` and is byte-identical; absent and `""` are indistinguishable
to every reader (`AuthenticationOptions.EnableDevLogin` already defaults to false;
`SignInProviderCatalog` treats blank as unset). The same reasoning applies to the sign-in keys for a
sharper reason: an always-emitted `Authentication__EnableDevLogin: "false"` **outranks the manifest**
and would silently discard the operator's answer, leaving a freshly set-up instance with no way in.

## The Homebrew install

`deploy/homebrew/share/values.local.yaml` used to answer both questions for you — dev login on,
`<your-username>` as the sole administrator, and a commented Entra block to uncomment. That is a
deployment making an identity decision on the operator's behalf, and getting it wrong in both
directions. Those keys are now blank, so `memex-local up` lands on the wizard and
`memex-local setup` hands over the URL and token.

Pinning an answer in the overlay still works and still wins — deployment configuration outranks the
manifest — which is how an unattended install skips the wizard.

## Related

- [Instance Identity and Setup](../InstanceIdentityAndSetup) — the four-slice design this completes
- [Model Providers](../ModelProviders) — where a provider credential lives once there is a mesh
- [Local development on Colima](../LocalColimaMac) — the manual steps `memex-local` automates
- [Access Control](../AccessControl) — what a platform admin is, and is not
