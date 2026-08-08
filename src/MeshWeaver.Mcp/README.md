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
