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

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 310" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arrowAI" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><path d="M0,0 L8,3 L0,6 Z" fill="#90caf9"/></marker>
    <marker id="arrowUser" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><path d="M0,0 L8,3 L0,6 Z" fill="#a5d6a7"/></marker>
    <marker id="arrowDown" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><path d="M0,0 L8,3 L0,6 Z" fill="currentColor" fill-opacity=".45"/></marker>
  </defs>
  <rect x="0" y="0" width="760" height="310" rx="16" fill="none" stroke="currentColor" stroke-opacity=".08" stroke-width="1"/>
  <rect x="40" y="20" width="170" height="80" rx="12" fill="#5c6bc0"/>
  <text x="125" y="52" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="15" font-weight="700">AI Agent</text>
  <text x="125" y="72" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="400" opacity=".85">Language model +</text>
  <text x="125" y="87" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="400" opacity=".85">agent definition (MeshNode)</text>
  <rect x="550" y="20" width="170" height="80" rx="12" fill="#26a69a"/>
  <text x="635" y="52" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="15" font-weight="700">Human User</text>
  <text x="635" y="72" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="400" opacity=".85">Browser / Blazor UI</text>
  <text x="635" y="87" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="400" opacity=".85">or MCP client</text>
  <rect x="200" y="30" width="155" height="60" rx="10" fill="#7b1fa2"/>
  <text x="277" y="54" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="13" font-weight="700">MeshPlugin</text>
  <text x="277" y="71" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="10" font-weight="400" opacity=".9">Get · Search · Create</text>
  <text x="277" y="84" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="10" font-weight="400" opacity=".9">Update · Delete · NavigateTo</text>
  <line x1="210" y1="60" x2="171" y2="60" stroke="#90caf9" stroke-width="2" marker-end="url(#arrowAI)"/>
  <line x1="355" y1="60" x2="549" y2="60" stroke="#a5d6a7" stroke-width="2" marker-end="url(#arrowUser)"/>
  <rect x="200" y="145" width="360" height="56" rx="12" fill="#1e88e5"/>
  <text x="380" y="168" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="700">Message API</text>
  <text x="380" y="187" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="400" opacity=".88">Access-controlled · Audited · Same for agents and users</text>
  <line x1="125" y1="100" x2="125" y2="165" stroke="#90caf9" stroke-width="1.5" stroke-dasharray="5 3"/>
  <line x1="125" y1="165" x2="199" y2="172" stroke="#90caf9" stroke-width="1.5" marker-end="url(#arrowAI)"/>
  <line x1="635" y1="100" x2="635" y2="165" stroke="#a5d6a7" stroke-width="1.5" stroke-dasharray="5 3"/>
  <line x1="635" y1="165" x2="561" y2="172" stroke="#a5d6a7" stroke-width="1.5" marker-end="url(#arrowUser)"/>
  <line x1="380" y1="201" x2="380" y2="229" stroke="currentColor" stroke-opacity=".45" stroke-width="2" marker-end="url(#arrowDown)"/>
  <rect x="140" y="230" width="480" height="64" rx="12" fill="#37474f"/>
  <text x="380" y="254" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="14" font-weight="700">Mesh</text>
  <rect x="155" y="261" width="100" height="24" rx="6" fill="#546e7a"/>
  <text x="205" y="277" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">MeshNodes</text>
  <rect x="270" y="261" width="100" height="24" rx="6" fill="#546e7a"/>
  <text x="320" y="277" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Business Logic</text>
  <rect x="385" y="261" width="100" height="24" rx="6" fill="#546e7a"/>
  <text x="435" y="277" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Permissions</text>
  <rect x="500" y="261" width="100" height="24" rx="6" fill="#546e7a"/>
  <text x="550" y="277" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Audit Trail</text>
</svg>

*AI agents and human users share the same access-controlled message API — the mesh treats them identically.*

---

## MeshPlugin tools

The MeshPlugin gives AI agents a small, composable toolset to work with the mesh:

| Tool | Purpose |
|------|---------|
| **Get** | Retrieve nodes by path (`@path`, `@path/*` for children, `@path/schema/` for schemas) |
| **Search** | Query nodes using GitHub-style syntax |
| **Create** | Create new nodes with validated MeshNode JSON |
| **Update** | Update existing nodes (Get → modify → Update) |
| **Delete** | Delete nodes by path |
| **NavigateTo** | Display a node's visual representation instead of raw JSON |

`Get` understands Unified Path prefixes — `@path/schema/` for the content's JSON Schema, `@path/model/` for the full data model. See [MeshPlugin Tools](Tools/MeshPlugin) for the full reference.

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

Slash skills work too: `/agent <name>`, `/model <name>`, `/harness <name>`.

---

## Explore further

| Topic | What you'll learn |
|-------|------------------|
| [Agentic AI](/Doc/AI/AgenticAI) | Philosophy, delegation vs handoff, human-agent collaboration patterns |
| [MeshPlugin Tools](/Doc/AI/Tools/MeshPlugin) | Full tool reference with call shapes and path syntax |
| [Execute Script](/Doc/AI/ExecuteScript) | How agents run C# scripts inside the mesh kernel |
| [MCP Authentication](/Doc/AI/McpAuthentication) | OAuth flow for external MCP clients connecting to MeshWeaver |
| [Provider Configuration](/Doc/AI/ProviderConfiguration) | Endpoints, keys, model tiers, and how to add a new provider |
| [Model Provider Settings](/Doc/AI/ModelProviderSettings) | Per-user and per-agent model selection in the Settings UI |
