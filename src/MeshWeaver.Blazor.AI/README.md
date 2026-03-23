# MeshWeaver.Blazor.AI

Exposes MeshWeaver mesh operations as a Model Context Protocol (MCP) server, enabling external AI tools (Claude Desktop, VS Code Copilot, etc.) to interact with the mesh over HTTP.

## Features

- MCP server with HTTP transport via `ModelContextProtocol.AspNetCore`
- Tools: `Get`, `Search`, `Create`, `Update`, `Delete`, and `NavigateTo` -- thin wrappers over `MeshOperations`
- Path autocomplete provider (`McpCompletionProvider`) for mesh paths and `@` references
- API token authentication via `RequireAuthorization("McpAuth")`
- Configurable base URL for generating NavigateTo links to the MeshWeaver UI

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

- `MeshWeaver.AI` -- `MeshOperations` for CRUD on mesh nodes
- `MeshWeaver.Graph` -- node type system and API token support
- `MeshWeaver.Messaging.Hub` -- message hub for request routing
- `ModelContextProtocol` / `ModelContextProtocol.AspNetCore` -- MCP server SDK
