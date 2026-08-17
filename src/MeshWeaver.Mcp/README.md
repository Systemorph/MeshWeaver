# MeshWeaver.Mcp

Exposes MeshWeaver mesh operations as a Model Context Protocol (MCP) server, enabling external AI tools (Claude Code, GitHub Copilot, Claude Desktop, etc.) to interact with the mesh over HTTP.

This is the transport-layer MCP module — **independent of Blazor** (it was previously `MeshWeaver.Blazor.AI`; that name was a misnomer — none of its files are Blazor components). It depends only on ASP.NET Core (`ModelContextProtocol.AspNetCore`) and the mesh feature modules, so any ASP.NET host — Blazor or not — can host it.

## It ships as a MODULE

No host compiles this in. `McpMeshModuleAttribute` folds `AddMeshMcp()` and `McpEndpointModuleAttribute` maps `/mcp` through the host's `MapMeshModuleEndpoints()` hook, so **listing `MeshWeaver.Mcp.dll` under `Modules:Assemblies` is the complete activation** (restart-required — see `Doc/Architecture/Modules.md`). Delisting removes the route wholesale: an MCP client gets a `404` it can act on, not a server with no mesh behind it.

Two things deliberately stay PLATFORM-side, because surfaces that outlive a delisted module depend on them:

- **The authentication scheme.** `McpAuthenticationExtensions` (the `McpAuth` and `MeshApiRead` policies, the ApiToken handler, the Bearer-only challenge) lives in the portal composition root — the REST mirror `/api/mesh/*` is gated by the same policies and is not part of this module, and an auth scheme has to be registered before the pipeline is built either way. This module names the policy by **string** only, so there is no compiled edge back to the host.
- **`SessionHubResolver` and `McpConfiguration`**, now in `MeshWeaver.Hosting.AspNetCore`. REST callers resolve the same per-caller hub (a second copy is exactly the drift the shared helper exists to prevent), and the co-hosted CLI back-connection reads the same `Mcp:BaseUrl`.

## Features

- MCP server with HTTP transport via `ModelContextProtocol.AspNetCore`
- Tools (thin, reactive wrappers over `MeshOperations` and the mesh hub): `Get`, `Search`, `Create`, `Update`, `Patch`, `Delete`, `Move`, `Copy`, `Upload`, `NavigateTo`, `Compile`, `ExecuteScript`, `StartThread`, `SubmitMessage`, `Mirror`, `Sync` (instance sync), `GitHubSync`, `RenderArea`, and the LSP/chunk tools
- Per-caller × session hub resolution (`SessionHubResolver`) — shared with the REST endpoint module so both transports route identically
- Path/`@`-reference autocomplete provider (`McpCompletionProvider`)
- API-token authentication via `RequireAuthorization("McpAuth")`
- Configurable base URL for generating NavigateTo links to the MeshWeaver UI

## Argument validation

Tool schemas change between releases (`create nodes` became `create node`, `delete path` became `delete paths`). A caller still speaking the older dialect used to get nothing useful: the SDK leaves `UnmappedMemberHandling` at `Skip`, so an unknown argument was **silently dropped** — and when the renamed parameter had a default, the call *ran* with that default and reported success. Anything the binder did throw on was flattened by the SDK into the fixed text `An error occurred invoking 'create'.`, which names nothing.

`McpArgumentValidation` closes that. It registers **one** `CallToolFilter` in `AddMeshMcp`, so every tool — present and future — is checked against its own published `InputSchema` before the binder runs, and the call is answered with the offending argument named:

- `Error: unknown argument 'nodes' for tool 'create' — expected one of: node. Did you mean 'node'? (Tool schemas change between releases — call tools/list for the current shape.)`
- `Error: missing required argument 'node' for tool 'create' — required: node.`
- `Error: argument 'limit' for tool 'search' expects integer, got string ("twenty").`

The check is deliberately conservative — it must never reject a call the binder would have accepted: JSON `null` passes through, numeric strings bind to `integer`/`number` (`NumberHandling = AllowReadingFromString`), and a parameter whose schema declares no `type` is not judged. The messages are model-facing and stay English by design (AGENTS.md: LLM-facing tool text is not localized).

## The `GitHubSync` tool

`github_sync` triggers a Space's GitHub sync headlessly — the same one-click Commit / Update / Check the browser's `GitHubAction` layout area runs. Each op runs as a mesh **Activity** (progress / cancel / persisted log) via `MeshWeaver.GitSync.GitHubActivityExtensions`; the tool fires the activity under the caller's identity and returns the activity handle immediately (it never blocks the MCP handler on the long-running GitHub I/O). Requires the Space's `_GitSync` config to exist.

## Usage

In a deployment, all of it is one line of configuration:

```jsonc
// appsettings.json
"Modules": { "Assemblies": [ "MeshWeaver.Mcp.dll" ] }
```

A fixture or a bespoke host that is not module-driven calls the same registrations directly — the
two lanes must never drift:

```csharp
// In MeshBuilder setup (ApiToken node type; AddGraph already does this in a portal)
builder.AddMcp();

// In service registration
services.AddMeshMcp();

// In endpoint mapping
app.MapMeshMcp("/mcp");
```

## Dependencies

- `MeshWeaver.AI` — `MeshOperations` for CRUD on mesh nodes
- `MeshWeaver.Graph` — node type system and API token support
- `MeshWeaver.GitSync` — the reactive GitHub sync activities behind `github_sync`
- `MeshWeaver.InstanceSync` — instance-sync registration behind the `Sync` tool
- `MeshWeaver.Messaging.Hub` — message hub for request routing
- `ModelContextProtocol` / `ModelContextProtocol.AspNetCore` — MCP server SDK
