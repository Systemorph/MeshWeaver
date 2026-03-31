# MeshWeaver.AI.ClaudeCode

Integrates the Claude Code CLI (Claude Agent SDK) as an AI backend for MeshWeaver's agent framework, bridging it to the `Microsoft.Extensions.AI` `IChatClient` interface.

## Features

- Implements `IChatClient` using the Claude Code CLI subprocess via `ClaudeAgentSdk`
- Streaming and non-streaming response modes with automatic text and tool-use extraction
- Configurable model selection (sonnet, opus, haiku), max turns, system prompt, and session timeout
- Automatic CLI path discovery with configurable `CliDirectory`
- Registers as an `IChatClientFactory` for the MeshWeaver agent framework

## Usage

```csharp
services.AddClaudeCode(config =>
{
    config.Models = ["sonnet", "opus"];
    config.CliDirectory = @"%APPDATA%\npm";
    config.MaxTurns = 10;
    config.SessionTimeoutMs = 120000;
});
```

Requires Claude Code CLI >= 2.0.0 installed via `npm install -g @anthropic-ai/claude-code`.

## Dependencies

- `MeshWeaver.AI` -- agent framework and `IChatClientFactory` base class
- `MeshWeaver.Messaging.Hub` -- message hub integration
- `ClaudeAgentSdk` -- .NET bindings for the Claude Code CLI
- `Microsoft.Extensions.AI` -- `IChatClient` abstraction
