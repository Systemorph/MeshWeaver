# MeshWeaver

MeshWeaver is an open-source framework for building **data meshes**: documents, data, code, AI agents, and UI live together as addressable nodes on a mesh — versioned, collaboratively editable, vector-searchable, and processed by an actor-model message hub with a reactive Blazor UI. **Memex**, the portal application included in this repository, is a knowledge portal built on it where people and AI agents work on the same content.

**See it live at [memex.meshweaver.cloud](https://memex.meshweaver.cloud)** — try the portal and read the full documentation at [memex.meshweaver.cloud/Doc](https://memex.meshweaver.cloud/Doc). The same docs are in this repo under [`src/MeshWeaver.Documentation/Data/`](src/MeshWeaver.Documentation/Data/).

## Installation

### Run from source

```bash
git clone https://github.com/Systemorph/MeshWeaver.git
cd MeshWeaver
dotnet run --project ../MeshWeaver.Plugins/src/Memex.Portal.Monolith
```

Opens at `https://localhost:7122`. Single process, no Docker — if you are unsure which setup to pick, pick this one.

For the microservices setup orchestrated with .NET Aspire (requires Docker):

```bash
dotnet run --project ../MeshWeaver.Plugins/src/Memex.AppHost
```

Portal at `https://localhost:7202`, with PostgreSQL persistence and the Aspire dashboard.

### Create your own portal

```bash
dotnet new install MeshWeaver.MemexTemplate
dotnet new meshweaver-memex -n MyPortal
cd MyPortal
dotnet run --project MyPortal.Portal.Monolith
```

> ⚠️ **The template is not on nuget.org right now** — see the NuGet section below.

The template contains the complete Memex portal solution (Blazor Server monolith + Aspire microservices, Graph + AI integration), renamed to your project name.

### Local Kubernetes stack (macOS)

A prod-like stack on Colima k3s. The default (local-build) path needs a checkout,
so the CLI runs straight from it:

```bash
./deploy/homebrew/bin/memex-local up
# …or symlink it onto your PATH: ln -s "$PWD/deploy/homebrew/bin/memex-local" ~/.local/bin/memex-local
```

Opens at `https://memex.localhost:8443`. A Homebrew install (via a local tap) is
also supported — see [`deploy/homebrew`](deploy/homebrew) for details.

### Deploy to production

Production deployment recipes live under [`deploy/`](deploy/):

- [`deploy/helm`](deploy/helm) — generic Kubernetes/Helm chart (Azure-free self-host)
- [`deploy/aks`](deploy/aks) — production-grade AKS sample (private cluster, P2S VPN, ACR, pgBackRest PITR)
- [`deploy/aca`](deploy/aca) — Azure Container Apps (Bicep)
- [`deploy/compose`](deploy/compose) · [`deploy/compose-ha`](deploy/compose-ha) — Docker Compose (single-node / HA)

**These are recipes, not the configuration of any running service.** Point them at your own
infrastructure: copy [`deploy/aks/envs/example`](deploy/aks/envs/example) to your own environment
folder and fill in your values. The files that carry real per-environment values —
`values.<env>.yaml`, `secretproviderclass.yaml` — are git-ignored and never committed here.

The AKS material is deliberately written as a **worked example** rather than an abstraction, so parts
of it name the cluster and hosts it was first proven on. Read those as illustration; they are not
config you inherit, and nothing in this repo is authoritative about a live installation.

If you are **self-hosting**, the recipes above plus your own values are the whole story.

If you are **operating an installation Systemorph runs**, its per-environment configuration and the
record of what runs where live in the private
**[Systemorph/Memex](https://github.com/Systemorph/Memex)** repo. Start there.

### CLI

The `memex` CLI operates any portal's mesh over the REST API — read, search, mutate, compile, and mirror nodes from the shell. It is not published as a dotnet tool; run it from a checkout:

```bash
dotnet run --project src/MeshWeaver.Cli -- login mw_yourtoken --base-url https://memex.meshweaver.cloud
dotnet run --project src/MeshWeaver.Cli -- search "nodeType:Agent"
```

See [`src/MeshWeaver.Cli`](src/MeshWeaver.Cli) for the full command reference. In CI the same verbs
come from the `mw-plugin-test` container image, which is what every pipeline here actually runs.

### NuGet packages

**MeshWeaver publishes exactly two packages**, and they are the two you start from:

| Package | What it does |
|---|---|
| [`MeshWeaver.MemexTemplate`](https://www.nuget.org/packages/MeshWeaver.MemexTemplate) | `dotnet new install MeshWeaver.MemexTemplate` — scaffolds a portal solution |
| [`MeshWeaver.Aspire.Hosting.Memex`](https://www.nuget.org/packages/MeshWeaver.Aspire.Hosting.Memex) | `builder.AddMemex()` — the Aspire integration that runs the portal, its database and its migration |

> ⚠️ **`MeshWeaver.MemexTemplate` is not resolvable right now.** Its last published version is
> `3.0.0-rc7` and every version of it is unlisted, so `dotnet new install MeshWeaver.MemexTemplate`
> cannot find it. Two reasons: its pack target was broken (it never passed the generator the
> platform checkout it requires — now fixed), and republishing it is blocked on an open decision,
> because the template can only be generated together with a UI project that currently lives in a
> private repository ([#3653](https://github.com/Systemorph/MeshWeaver/issues/3653) and
> [NuGet Package Retirement](src/MeshWeaver.Documentation/Data/Architecture/NuGetPackageRetirement.md)).
> Until that is answered, generate the template from a checkout —
> `tools/generate-memex-template.cs` in MeshWeaver.Plugins.

The framework itself is **not** distributed as libraries. It ships as a container image: your mesh
content compiles against the running portal, plugins ship as module bundles, and startup
configuration is expressed as options on the Aspire integration rather than as package references.
The forty-three `MeshWeaver.*` library packages published up to `3.0.0-rc13` are retired and unlisted
— existing exact-version pins keep resolving, but nothing new is published under them. See
[NuGet Package Retirement](src/MeshWeaver.Documentation/Data/Architecture/NuGetPackageRetirement.md).

## Community

Licensed under [MIT](LICENSE).
