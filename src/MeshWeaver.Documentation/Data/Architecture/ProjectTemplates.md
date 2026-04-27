---
Name: Project Templates
Category: Architecture
Description: Bootstrapping a new MeshWeaver portal with dotnet new, template structure, configuration, and customization
Icon: /static/NodeTypeIcons/code.svg
---

MeshWeaver ships a **.NET project template** (`meshweaver-memex`) that scaffolds a complete portal application. Running `dotnet new meshweaver-memex -o MyProject` generates a working solution with sample data, authentication, AI integration, and both monolith and distributed deployment options — ready to run in under a minute.

# Why Templates

Building a MeshWeaver portal from scratch requires configuring message hubs, layout areas, authentication, graph nodes, access control, and Aspire orchestration. The template handles all of this, giving you:

- A **running portal** with sample data (ACME insurance company demo)
- **Dev login** with pre-configured users (Admin, Alice, Bob) for local development
- **Two deployment modes**: monolith (single process, no dependencies) and distributed (Aspire + Orleans + PostgreSQL)
- **Proper namespace renaming**: `dotnet new` replaces `Memex` with your project name throughout

# Quick Start

## Install the Template

From a published NuGet package:

```bash
dotnet new install MeshWeaver.ProjectTemplates
```

Or from a local template directory (for development):

```bash
dotnet new install path/to/dist/templates/
```

## Scaffold a New Project

```bash
dotnet new meshweaver-memex -o MyProject
```

This creates a `MyProject/` directory with all projects renamed from `Memex` to `MyProject`.

## Run the Monolith Portal

```bash
dotnet run --project MyProject/MyProject.Portal.Monolith
```

Open the URL shown in the console (default: `https://localhost:7222`). The dev login page lists available users — click one to sign in.

## Run with Aspire (Distributed)

```bash
dotnet run --project MyProject/aspire/MyProject.AppHost
```

This starts the Aspire dashboard with PostgreSQL (Docker), the distributed portal with Orleans, and the database migration service.

# What the Template Generates

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

# Template Architecture

## Two User Scopes

The template includes users at two levels, mirroring the MeshWeaver convention:

| Scope | Path | Purpose |
|-------|------|---------|
| **Global** | `User/Admin`, `User/Alice`, `User/Bob` | Portal-wide login users with `namespace: "User"` |
| **Partition** | `ACME/User/Oliver`, `ACME/User/Paul`, `ACME/User/Quinn` | Organization-scoped users with `namespace: "ACME/User"` |

The **DevLogin page** queries `nodeType:User namespace:User` — only global users appear. Partition-scoped users (like ACME's Oliver) are visible within their organization context but do not appear on the login screen.

## Access Control

Each login user needs an **AccessAssignment** node granting a role. These live in `User/_Access/`:

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

Without an access assignment, the user can log in but receives "Access denied" errors when navigating. The template ships Admin, Alice, and Bob with the `Admin` role.

## Graph Storage Configuration

The monolith portal loads sample data from the filesystem via `appsettings.Development.json`:

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

Paths are relative to the monolith project directory. The distributed portal uses PostgreSQL instead — no file paths needed.

## What the Framework Provides

`AddGraph()` and `AddDocumentation()` (called in the shared configuration) register built-in resources that are **not** in the template's `samples/` directory:

- **Node types**: Markdown, Code, Agent, Group, User, VUser, Role, Notification, Approval, AccessAssignment, GroupMembership, and more
- **Documentation**: Architecture guides, DataMesh reference, GUI controls, AI integration docs (served under the `Doc/` namespace)
- **Icons**: Node type icons (`/static/NodeTypeIcons/`)
- **Roles**: Built-in Admin, Editor, Viewer role definitions

# Customizing Your Portal

## Adding Users

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

## Adding a New Organization

Create a directory under `samples/Graph/Data/` following the ACME pattern:

```
samples/Graph/Data/MyOrg/
├── MyOrg.json              # Organization root node
├── Project/                # Projects
├── User/                   # Org-scoped users
├── Doc/                    # Org documentation
└── _Access/                # Org-level access assignments
```

## Replacing Sample Data

Delete the `ACME/` directory and add your own data. The portal loads whatever is in the `samples/Graph/Data/` directory — there are no hard-coded references to ACME.

## Moving to Production Auth

The DevLogin page is only active when `ASPNETCORE_ENVIRONMENT=Development`. In production, configure Microsoft or Google OAuth in the Aspire AppHost. See [Deployment](Deployment) for secrets and redirect URI setup.

# Monolith vs Distributed

| Aspect | Monolith | Distributed (Aspire) |
|--------|----------|---------------------|
| **Dependencies** | None | Docker (PostgreSQL, Azurite) |
| **Data storage** | Filesystem (`samples/Graph/Data/`) | PostgreSQL with pgvector |
| **Scaling** | Single process | Orleans clustering, Azure Container Apps |
| **Use case** | Local development, demos | Staging, production |
| **Run command** | `dotnet run --project MyProject.Portal.Monolith` | `dotnet run --project aspire/MyProject.AppHost` |

Start with the monolith for development. When you need persistence, search, or multi-instance scaling, switch to the distributed mode. Both share the same `MyProject.Portal.Shared` project — all UI, configuration, and business logic is identical.

# Troubleshooting

## "Address already in use" on startup

The default ports (7222/5222) are occupied. Either stop the other process or change the ports in `Properties/launchSettings.json`.

## Dev login shows no users

The DevLogin page queries `nodeType:User namespace:User`. Ensure you have user JSON files in `samples/Graph/Data/User/` (not just inside an organization like `ACME/User/`).

## "Access denied" after login

The user exists but has no access assignment. Create an `AccessAssignment` node in `User/_Access/` granting a role (Admin, Editor, or Viewer).

## Portal crashes on startup (missing Graph:Storage)

`ASPNETCORE_ENVIRONMENT` is not set to `Development`. Ensure `Properties/launchSettings.json` exists and sets the environment variable, or run with `--environment Development`.

## ACME data not loading

Check that `appsettings.Development.json` has correct relative paths. From the monolith project directory, `../samples/Graph/Data` should resolve to the `samples/` folder at the solution root.
