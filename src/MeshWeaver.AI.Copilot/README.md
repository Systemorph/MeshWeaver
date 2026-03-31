# MeshWeaver.AI.Copilot

Integrates the GitHub Copilot SDK as an AI backend for MeshWeaver's agent framework, bridging its event-based session model to the `Microsoft.Extensions.AI` `IChatClient` interface.

## Features

- Implements `IChatClient` using the GitHub Copilot SDK with event-driven streaming via channels
- Supports streaming responses with delta events, tool execution events, and session lifecycle management
- Configurable model selection, CLI path/URL, port, and session timeout
- Auto-start and auto-restart of the Copilot client process
- Passes system messages as session-level `SystemMessage` configuration and forwards `AIFunction` tools

## Usage

```csharp
services.AddCopilot(config =>
{
    config.Models = ["gpt-4o", "claude-sonnet"];
    config.EnableStreaming = true;
    config.SessionTimeoutMs = 30000;
});
```

Requires the GitHub Copilot CLI to be installed and available in PATH.

## Dependencies

- `MeshWeaver.AI` -- agent framework and `IChatClientFactory` base class
- `MeshWeaver.Messaging.Hub` -- message hub integration
- `GitHub.Copilot.SDK` -- .NET bindings for the GitHub Copilot CLI
- `Microsoft.Extensions.AI` -- `IChatClient` abstraction
