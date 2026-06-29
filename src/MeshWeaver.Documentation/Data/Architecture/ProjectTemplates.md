---
Name: Project Templates
Category: Architecture
Description: Bootstrapping a new MeshWeaver portal with dotnet new, template structure, configuration, and customization
Icon: /static/NodeTypeIcons/code.svg
---

MeshWeaver ships a **.NET project template** (`meshweaver-memex`) that scaffolds a complete, runnable portal in one command. `dotnet new meshweaver-memex -o MyProject` produces a working solution with sample data, authentication, AI integration, and both monolith and distributed deployment options — ready to run in under a minute.

## Why Use the Template?

Building a MeshWeaver portal from scratch means wiring up message hubs, layout areas, authentication, graph nodes, access control, and Aspire orchestration. The template handles all of that up front, giving you:

- A **running portal** with sample data (an ACME insurance company demo) from day one
- **Dev login** with pre-configured users (Admin, Alice, Bob) for frictionless local development
- **Two deployment modes** — a lightweight monolith with no external dependencies, and a full distributed stack with Aspire, Orleans, and PostgreSQL
- **Proper namespace renaming** — `dotnet new` replaces every `Memex` reference with your project name throughout the generated solution

## Quick Start

### 1. Install the Template

From NuGet:

```bash
dotnet new install MeshWeaver.ProjectTemplates
```

Or from a local template directory (useful during development of the template itself):

```bash
dotnet new install path/to/dist/templates/
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

Open the URL shown in the console (default: `https://localhost:7222`). The dev login page lists available users — click any name to sign in immediately.

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
│   ├── Properties/launchSettings.json      # Dev environment & ports
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
│   │   ├── Project/                        # 2 projects with Todos
│   │   ├── User/                           # 3 org-scoped users (Oliver, Paul, Quinn)
│   │   ├── Agent/                          # TodoAgent definition
│   │   ├── Doc/                            # ACME-specific documentation
│   │   └── _Access/                        # Partition-level access assignments
│   └── User/                               # Top-level login users
│       ├── Admin.json                      # Admin user
│       ├── Alice.json                      # Sample user
│       ├── Bob.json                        # Sample user
│       └── _Access/                        # Global access assignments (Admin role)
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

The **DevLogin page** queries `nodeType:User namespace:User` — so only global users appear at the login screen. Partition-scoped users (such as ACME's Oliver) are visible within their organization context but do not surface on the login page.

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

> Without an access assignment, a user can log in but receives "Access denied" on every page. The template ships Admin, Alice, and Bob pre-configured with the `Admin` role.

### Graph Storage Configuration

The monolith portal loads sample data from the filesystem. Paths are declared in `appsettings.Development.json`, relative to the monolith project directory:

```json
{
  "Graph": {
    "Storage": {
      "Type": "FileSystem",
      "BasePath": "../samples/Graph/Data"
    },
    "Content": {
      "Type": "FileSystem",
      "BasePath": "../samples/Graph"
    }
  }
}
```

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

The DevLogin page is active only when `ASPNETCORE_ENVIRONMENT=Development`. In production, configure Microsoft or Google OAuth in the Aspire AppHost. See [Deployment](/Doc/Architecture/Deployment) for secrets management and redirect URI setup.

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

The default ports (7222/5222) are occupied by another process. Either stop that process or change the ports in `Properties/launchSettings.json`.

### Dev login shows no users

The DevLogin page queries `nodeType:User namespace:User`. Make sure your user JSON files live in `samples/Graph/Data/User/` — files inside an organization subdirectory like `ACME/User/` will not appear.

### "Access denied" after login

The user node exists but has no access assignment. Create an `AccessAssignment` node in `User/_Access/` granting the user a role (Admin, Editor, or Viewer).

### Portal crashes on startup (missing `Graph:Storage`)

`ASPNETCORE_ENVIRONMENT` is not set to `Development`. Verify that `Properties/launchSettings.json` exists and sets the environment variable, or pass `--environment Development` on the command line.

### ACME data not loading

Check that `appsettings.Development.json` has correct relative paths. From the monolith project directory, `../samples/Graph/Data` should resolve to the `samples/` folder at the solution root.
