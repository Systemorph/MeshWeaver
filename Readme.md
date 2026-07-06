# MeshWeaver

MeshWeaver is an open-source framework for building **data meshes**: documents, data, code, AI agents, and UI live together as addressable nodes on a mesh — versioned, collaboratively editable, vector-searchable, and processed by an actor-model message hub with a reactive Blazor UI. **Memex**, the portal application included in this repository, is a knowledge portal built on it where people and AI agents work on the same content.

**See it live at [memex.meshweaver.cloud](https://memex.meshweaver.cloud)** — try the portal and read the full documentation at [memex.meshweaver.cloud/Doc](https://memex.meshweaver.cloud/Doc). The same docs are in this repo under [`src/MeshWeaver.Documentation/Data/`](src/MeshWeaver.Documentation/Data/).

## Installation

### Run from source

```bash
git clone https://github.com/Systemorph/MeshWeaver.git
cd MeshWeaver
dotnet run --project memex/Memex.Portal.Monolith
```

Opens at `https://localhost:7122`. Single process, no Docker — if you are unsure which setup to pick, pick this one.

For the microservices setup orchestrated with .NET Aspire (requires Docker):

```bash
dotnet run --project memex/aspire/Memex.AppHost
```

Portal at `https://localhost:7202`, with PostgreSQL persistence and the Aspire dashboard.

### Create your own portal

```bash
dotnet new install MeshWeaver.MemexTemplate
dotnet new meshweaver-memex -n MyPortal
cd MyPortal
dotnet run --project MyPortal.Portal.Monolith
```

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

### CLI

The `memex` CLI operates any portal's mesh over the REST API — read, search, mutate, compile, and mirror nodes from the shell:

```bash
dotnet tool install -g MeshWeaver.Cli --prerelease
memex login mw_yourtoken --base-url https://memex.meshweaver.cloud
memex search "nodeType:Agent"
```

See [`src/MeshWeaver.Cli`](src/MeshWeaver.Cli) for the full command reference.

### NuGet packages

The framework ships as [`MeshWeaver.*` packages on nuget.org](https://www.nuget.org/packages?q=MeshWeaver) — use them to build your own modules, node types, and agents against the mesh.

## Community

Join our [Discord](https://discord.gg/wMTug8qtvc) to discuss features, report issues, or get help. Licensed under [MIT](LICENSE).
