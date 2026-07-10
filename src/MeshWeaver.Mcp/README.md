# MeshWeaver.Mcp

Exposes MeshWeaver mesh operations as a Model Context Protocol (MCP) server, enabling external AI tools (Claude Code, GitHub Copilot, Claude Desktop, etc.) to interact with the mesh over HTTP.

This is the transport-layer MCP module — **independent of Blazor** (it was previously `MeshWeaver.Blazor.AI`; that name was a misnomer — none of its files are Blazor components). It depends only on ASP.NET Core (`ModelContextProtocol.AspNetCore`) and the mesh feature modules, so any ASP.NET host — Blazor or not — can host it.

## Features

- MCP server with HTTP transport via `ModelContextProtocol.AspNetCore`
- Tools (thin, reactive wrappers over `MeshOperations` and the mesh hub): `Get`, `Search`, `Create`, `Update`, `Patch`, `Delete`, `Move`, `Copy`, `Upload`, `NavigateTo`, `Compile`, `ExecuteScript`, `StartThread`, `SubmitMessage`, `Mirror`, `Sync` (instance sync), `GitHubSync`, `RenderArea`, and the LSP/chunk tools
- Per-caller × session hub resolution (`SessionHubResolver`) — shared with the REST endpoint module so both transports route identically
- Path/`@`-reference autocomplete provider (`McpCompletionProvider`)
- API-token authentication via `RequireAuthorization("McpAuth")`
- Configurable base URL for generating NavigateTo links to the MeshWeaver UI

## The `GitHubSync` tool

`github_sync` triggers a Space's GitHub sync headlessly — the same one-click Commit / Update / Check the browser's `GitHubAction` layout area runs. Each op runs as a mesh **Activity** (progress / cancel / persisted log) via `MeshWeaver.GitSync.GitHubActivityExtensions`; the tool fires the activity under the caller's identity and returns the activity handle immediately (it never blocks the MCP handler on the long-running GitHub I/O). Requires the Space's `_GitSync` config to exist.

## Usage

```csharp
// In MeshBuilder setup
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
