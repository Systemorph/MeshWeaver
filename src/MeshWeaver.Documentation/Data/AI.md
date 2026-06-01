---
Name: AI Integration
Category: Documentation
Description: AI agents, MeshPlugin tools, and natural-language interfaces that let agents work alongside your data mesh
Icon: /static/DocContent/AI/icon.svg
---

<div style="background: linear-gradient(135deg, #4527a0 0%, #7b1fa2 100%); border-radius: 18px; padding: 40px 34px; margin: 4px 0 30px 0; color: #fff;">
  <div style="font-size: 2.1rem; font-weight: 800; letter-spacing: -0.02em; line-height: 1.15;">AI Integration</div>
  <div style="font-size: 1.05rem; opacity: 0.92; margin-top: 10px; max-width: 720px; line-height: 1.55;">
    Agents, tools, and natural-language interfaces. AI <em>remote-controls</em> the mesh through the same message-based APIs as users — business logic stays independent of AI code.
  </div>
</div>

## How it fits together

MeshWeaver treats AI agents as first-class participants in the mesh, not as a bolt-on layer. An agent reads and writes nodes through the same access-controlled message APIs that a human user does. This means business rules, permissions, and audit trails apply uniformly — the mesh doesn't know or care whether a request came from a browser or a language model.

Three ideas anchor the design:

- **Agents are data.** Agent definitions are versioned MeshNodes, editable without code changes.
- **Tools are composable.** The MeshPlugin gives every agent a small, orthogonal toolset for reading, writing, and navigating the mesh.
- **Models are swappable.** Provider configuration and model selection are separated from agent logic, so you can tune one without touching the other.

---

## MeshPlugin tools

The MeshPlugin gives AI agents a small, composable toolset to work with the mesh:

| Tool | Purpose |
|------|---------|
| **Get** | Retrieve nodes by path (`@path`, `@path/*` for children, `@path/schema:` for schemas) |
| **Search** | Query nodes using GitHub-style syntax |
| **Create** | Create new nodes with validated MeshNode JSON |
| **Update** | Update existing nodes (Get → modify → Update) |
| **Delete** | Delete nodes by path |
| **NavigateTo** | Display a node's visual representation instead of raw JSON |

`Get` understands Unified Path prefixes — `@path/schema:` for the content's JSON Schema, `@path/model:` for the full data model. See [MeshPlugin Tools](Tools/MeshPlugin) for the full reference.

---

## Agents are data

Agents are markdown MeshNodes with `nodeType: Agent`, so they are versioned alongside your data and updated without code changes:

```yaml
---
nodeType: Agent
name: Todo Agent
description: Manages tasks for ACME projects
icon: TaskListSquare
isDefault: true
---

Instructions for the agent...
```

Because agents go through the standard message interfaces, they are **context-aware** (they query the mesh for the current namespace, team, and schemas) and **subject to the same access control** as any user.

---

## Selecting agents and models in chat

Use unified reference syntax to steer a conversation:

```
@agent/Documentation          select an agent
@model/claude-haiku-4-5        select a model
@agent/RiskImportAgent import Microsoft.xlsx   combine selection with a prompt
```

Slash commands work too: `/agent <name>`, `/model <name>`, `/help`.

---

## Explore further

| Topic | What you'll learn |
|-------|------------------|
| [Agentic AI](AI/AgenticAI) | Philosophy, delegation vs handoff, human-agent collaboration patterns |
| [MeshPlugin Tools](AI/Tools/MeshPlugin) | Full tool reference with call shapes and path syntax |
| [Execute Script](AI/ExecuteScript) | How agents run C# scripts inside the mesh kernel |
| [MCP Authentication](AI/McpAuthentication) | OAuth flow for external MCP clients connecting to MeshWeaver |
| [Provider Configuration](AI/ProviderConfiguration) | Endpoints, keys, model tiers, and how to add a new provider |
| [Model Provider Settings](AI/ModelProviderSettings) | Per-user and per-agent model selection in the Settings UI |
