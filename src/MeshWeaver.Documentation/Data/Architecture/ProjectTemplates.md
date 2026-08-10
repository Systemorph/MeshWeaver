---
Name: Project Templates
Category: Architecture
Description: Bootstrapping a new MeshWeaver portal with dotnet new, template structure, configuration, and customization
Icon: /static/NodeTypeIcons/code.svg
---

MeshWeaver ships a **.NET project template** — package id `MeshWeaver.MemexTemplate`, short name `meshweaver-memex` — that scaffolds a complete, runnable portal in one command. `dotnet new meshweaver-memex -o MyProject` produces a working solution with sample data, authentication, AI integration, and both monolith and distributed deployment options — ready to run in under a minute.

## Why Use the Template?

Building a MeshWeaver portal from scratch means wiring up message hubs, layout areas, authentication, graph nodes, access control, and Aspire orchestration. The template handles all of that up front, giving you:

- A **running portal** with sample data (an ACME insurance company demo) from day one
- **Dev login** with pre-configured sample users (Alice, Bob, Carol, David, Emma, TestUser) for frictionless local development
- **Two deployment modes** — a lightweight monolith with no external dependencies, and a full distributed stack with Aspire, Orleans, and PostgreSQL
- **Proper namespace renaming** — `dotnet new` replaces every `Memex` reference with your project name throughout the generated solution

## Quick Start

### 1. Install the Template

From NuGet:

```bash
dotnet new install MeshWeaver.MemexTemplate
```

Or build it from this repository (useful when working on the template itself) — packing runs
`tools/generate-memex-template.cs`, which regenerates the template content from the live `memex/`
source tree into `dist/templates/`, then packs that directory:

```bash
dotnet pack tools/MeshWeaver.MemexTemplate.Pack
dotnet new install path/to/dist/templates/          # or install the produced .nupkg
```

### 2. Scaffold a New Project

```bash
dotnet new meshweaver-memex -o MyProject
```

This creates a `MyProject/` directory with all projects renamed from `Memex` to `MyProject`.

### 3. Run the Monolith Portal

```bash
dotnet run --project MyProject/MyProject.Portal.Monolith
```

Open the URL shown in the console (the generated README says `https://localhost:7122`). The dev login page lists available users — click any name to sign in immediately.

> The template does **not** ship a `Properties/launchSettings.json` (it is gitignored in the source tree the generator copies from), so `dotnet run` uses the ASP.NET defaults unless you add one. Set `ASPNETCORE_ENVIRONMENT=Development` yourself — either by adding a `launchSettings.json` or with `--environment Development` — otherwise the portal starts without the dev configuration it needs.

### 4. Run with Aspire (Distributed)

```bash
dotnet run --project MyProject/aspire/MyProject.AppHost
```

This launches the Aspire dashboard together with PostgreSQL (via Docker), the distributed portal with an Orleans silo, and the database migration service.

## What Gets Generated

```
MyProject/
├── MyProject.slnx                          # Solution file
├── MyProject.Portal.Monolith/              # Standalone portal (no external deps)
│   ├── Program.cs                          # Entry point
│   └── appsettings.Development.json        # Graph storage paths, AI config
├── MyProject.Portal.Shared/                # Shared Razor UI, auth, configuration
│   ├── Pages/                              # DevLogin, Onboarding, portal pages
│   ├── Authentication/                     # DevAuthController, middleware
│   └── MyProjectConfiguration.cs           # Hub setup, AddGraph(), AddDocumentation()
├── aspire/
│   ├── MyProject.AppHost/                  # Aspire orchestrator
│   ├── MyProject.Portal.Distributed/       # Portal with Orleans silo
│   ├── MyProject.Database.Migration/       # Schema migration (run-to-completion)
│   └── MyProject.Portal.ServiceDefaults/   # Health, telemetry defaults
├── samples/Graph/Data/                     # Sample data loaded by AddGraph()
│   ├── ACME/                               # Insurance company demo
│   │   ├── Project/                        # Projects
│   │   ├── Article/ ProductLaunch/         # Sample content nodes
│   │   ├── Documentation/                  # ACME-specific documentation
│   │   ├── User/                           # 3 org-scoped users (Oliver, Paul, Quinn)
│   │   └── _Access/                        # Partition-level access assignments
│   └── User/                               # Top-level login users
│       ├── Alice.json  Bob.json  …         # Sample users (Roland/Samuel are excluded by the generator)
│       └── _Access/                        # Global access assignments
├── Directory.Build.props                   # MSBuild properties
├── Directory.Packages.props                # Centralized NuGet versions
└── nuget.config                            # Package sources
```

## Template Architecture

### Two User Scopes

The template ships users at two levels, mirroring MeshWeaver's built-in user convention:

| Scope | Path | Purpose |
|-------|------|---------|
| **Global** | `User/Admin`, `User/Alice`, `User/Bob` | Portal-wide login users with `namespace: "User"` |
| **Partition** | `ACME/User/Oliver`, `ACME/User/Paul`, `ACME/User/Quinn` | Organization-scoped users with `namespace: "ACME/User"` |

The **DevLogin page** lists users through `AccessSubjectQueries.Users` — the one canonical users query, `nodeType:User namespace:""`. 🚨 Do **not** hand-roll `nodeType:User namespace:User`: that legacy shape targets the pre-V27 `user` schema, which no longer exists, and it silently returns **zero** users (issue #213). Always reference `AccessSubjectQueries.Users` rather than re-typing a query.

### Access Control

Every login user needs an **AccessAssignment** node that grants a role. These live under `User/_Access/`:

```json
{
  "id": "Admin_Access",
  "namespace": "User/_Access",
  "nodeType": "AccessAssignment",
  "content": {
    "$type": "AccessAssignment",
    "accessObject": "Admin",
    "displayName": "Admin",
    "roles": [{ "role": "Admin" }]
  }
}
```

> Without an access assignment, a user can log in but receives "Access denied" on every page. In the sample data, `User/_Access/` carries the global assignments (e.g. `TestUser_Access` with the `Admin` role) and `ACME/_Access/` the partition-scoped ones.

### Graph Storage Configuration

The monolith portal loads sample data from the filesystem. Paths are declared in `appsettings.Development.json`, relative to the monolith project directory:

```json
{
  "Graph": {
    "Storage": {
      "Type": "FileSystem",
      "BasePath": "../samples/Graph/Data"
    }
  },
  "Storage": {
    "Name": "storage",
    "SourceType": "FileSystem",
    "BasePath": "../samples/Graph"
  }
}
```

Note the two sections are **siblings**: `Graph:Storage` is the node store, and the top-level
`Storage` section is the content/blob store — there is no `Graph:Content`. The generator rewrites the
`BasePath` values from `../../` (their depth inside `memex/`) to `../` when it emits the template.

The distributed portal uses PostgreSQL instead — no file paths required.

### What the Framework Provides Out of the Box

`AddGraph()` and `AddDocumentation()`, called in the shared configuration, register built-in resources that are **not** part of the template's `samples/` directory. You get these automatically:

| Resource | Details |
|----------|---------|
| **Node types** | Markdown, Code, Agent, Group, User, VUser, Role, Notification, Approval, AccessAssignment, GroupMembership, and more |
| **Documentation** | Architecture guides, DataMesh reference, GUI controls, AI integration docs (served under `Doc/`) |
| **Icons** | Node type icons at `/static/NodeTypeIcons/` |
| **Roles** | Built-in Admin, Editor, and Viewer role definitions |

## Customizing Your Portal

### Adding Users

Create a JSON file in `samples/Graph/Data/User/` and a matching access assignment in `User/_Access/`:

```json
{
  "id": "Jane",
  "namespace": "User",
  "name": "Jane Doe",
  "nodeType": "User",
  "icon": "/static/NodeTypeIcons/person.svg",
  "isPersistent": true,
  "content": {
    "$type": "User",
    "email": "jane@example.com",
    "bio": "Product manager."
  }
}
```

```json
{
  "id": "Jane_Access",
  "namespace": "User/_Access",
  "nodeType": "AccessAssignment",
  "content": {
    "$type": "AccessAssignment",
    "accessObject": "Jane",
    "displayName": "Jane Doe",
    "roles": [{ "role": "Admin" }]
  }
}
```

### Adding a New Organization

Mirror the ACME structure under `samples/Graph/Data/`:

```
samples/Graph/Data/MyOrg/
├── MyOrg.json              # Organization root node
├── Project/                # Projects
├── User/                   # Org-scoped users
├── Doc/                    # Org documentation
└── _Access/                # Org-level access assignments
```

### Replacing the Sample Data

Delete the `ACME/` directory and add your own data. The portal loads whatever is in `samples/Graph/Data/` — there are no hard-coded references to ACME anywhere in the framework.

### Moving to Production Auth

🚨 **DevLogin is not gated on the environment.** It is enabled by the resolved *authentication provider*: `Auth:EnableDevLogin` when set, otherwise `true` whenever the provider resolves to `Dev` — which is the **fallback** when no external providers and no Entra ID configuration are present. So a portal deployed with `ASPNETCORE_ENVIRONMENT=Production` but no auth configured still serves the dev login page.

Before going to production, configure a real provider (Entra ID / an external OAuth provider) — and set `Auth:EnableDevLogin=false` explicitly if you want belt-and-braces. See [Deployment](/Doc/Architecture/Deployment) for secrets management and redirect URI setup.

## Monolith vs. Distributed

| Aspect | Monolith | Distributed (Aspire) |
|--------|----------|---------------------|
| **Dependencies** | None | Docker (PostgreSQL, Azurite) |
| **Data storage** | Filesystem (`samples/Graph/Data/`) | PostgreSQL with pgvector |
| **Scaling** | Single process | Orleans clustering, Azure Container Apps |
| **Primary use case** | Local development, demos | Staging, production |
| **Run command** | `dotnet run --project MyProject.Portal.Monolith` | `dotnet run --project aspire/MyProject.AppHost` |
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 320" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <text x="185" y="22" text-anchor="middle" font-size="14" font-weight="bold" fill="currentColor" fill-opacity=".85">Monolith</text>
  <text x="575" y="22" text-anchor="middle" font-size="14" font-weight="bold" fill="currentColor" fill-opacity=".85">Distributed (Aspire)</text>
  <rect x="20" y="34" width="330" height="260" rx="12" fill="none" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" stroke-dasharray="6,4"/>
  <rect x="410" y="34" width="330" height="260" rx="12" fill="none" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5" stroke-dasharray="6,4"/>
  <rect x="85" y="50" width="200" height="38" rx="8" fill="#1e88e5"/>
  <text x="185" y="74" text-anchor="middle" fill="#fff" font-weight="bold">Portal.Monolith</text>
  <rect x="85" y="112" width="200" height="38" rx="8" fill="#5c6bc0"/>
  <text x="185" y="136" text-anchor="middle" fill="#fff" font-weight="bold">Portal.Shared</text>
  <rect x="85" y="174" width="200" height="38" rx="8" fill="#43a047"/>
  <text x="185" y="198" text-anchor="middle" fill="#fff" font-weight="bold">samples/Graph/Data/</text>
  <rect x="110" y="236" width="70" height="34" rx="7" fill="#26a69a"/>
  <text x="145" y="258" text-anchor="middle" fill="#fff" font-size="11">Filesystem</text>
  <rect x="195" y="236" width="70" height="34" rx="7" fill="#26a69a"/>
  <text x="230" y="258" text-anchor="middle" fill="#fff" font-size="11">In-memory</text>
  <line x1="185" y1="88" x2="185" y2="112" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="185" y1="150" x2="185" y2="174" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="155" y1="212" x2="145" y2="236" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="215" y1="212" x2="230" y2="236" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="450" y="50" width="160" height="34" rx="8" fill="#f57c00"/>
  <text x="530" y="72" text-anchor="middle" fill="#fff" font-weight="bold">AppHost (Aspire)</text>
  <rect x="450" y="112" width="160" height="34" rx="8" fill="#1e88e5"/>
  <text x="530" y="134" text-anchor="middle" fill="#fff" font-weight="bold">Portal.Distributed</text>
  <rect x="450" y="164" width="160" height="34" rx="8" fill="#5c6bc0"/>
  <text x="530" y="186" text-anchor="middle" fill="#fff" font-weight="bold">Portal.Shared</text>
  <rect x="430" y="222" width="90" height="34" rx="7" fill="#8e24aa"/>
  <text x="475" y="244" text-anchor="middle" fill="#fff" font-size="11">Orleans Silo</text>
  <rect x="535" y="222" width="90" height="34" rx="7" fill="#e53935"/>
  <text x="580" y="240" text-anchor="middle" fill="#fff" font-size="11">PostgreSQL</text>
  <text x="580" y="252" text-anchor="middle" fill="#fff" font-size="10">+ pgvector</text>
  <line x1="530" y1="84" x2="530" y2="112" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="530" y1="146" x2="530" y2="164" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="490" y1="198" x2="475" y2="222" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="570" y1="198" x2="580" y2="222" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
</svg>
*Both modes share `Portal.Shared` — identical UI, auth, and business logic. Switch from monolith to distributed by changing the run command.*

Start with the monolith during development — it has no external dependencies and restarts in seconds. When you need persistence, full-text search, vector search, or multi-instance scaling, switch to distributed mode. Both share the same `MyProject.Portal.Shared` project, so all UI, configuration, and business logic is identical across the two modes.

## Troubleshooting

### "Address already in use" on startup

The port the portal binds is occupied by another process. Either stop that process, or set the URLs explicitly (`--urls "https://localhost:7123;http://localhost:5023"`, or in a `Properties/launchSettings.json` you add).

### Dev login shows no users

The DevLogin page lists users via `AccessSubjectQueries.Users`. Make sure your user JSON files live in `samples/Graph/Data/User/` and carry `"nodeType": "User"`. If you have copied the query into your own code, check it is not the legacy `namespace:User` shape — that one returns zero rows.

### "Access denied" after login

The user node exists but has no access assignment. Create an `AccessAssignment` node in `User/_Access/` granting the user a role (Admin, Editor, or Viewer).

### Portal crashes on startup (missing `Graph:Storage`)

`ASPNETCORE_ENVIRONMENT` is not set to `Development`, so `appsettings.Development.json` — which is where the storage paths live — is never layered in. The template ships no `launchSettings.json`, so pass `--environment Development` on the command line or add one that sets the variable.

### ACME data not loading

Check that `appsettings.Development.json` has correct relative paths. From the monolith project directory, `../samples/Graph/Data` should resolve to the `samples/` folder at the solution root.
