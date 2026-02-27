---
Name: AI Integration
Category: Documentation
Description: AI agents, MeshPlugin tools, and natural language interfaces for MeshWeaver applications
Icon: /static/storage/content/MeshWeaver/Documentation/AI/icon.svg
---

MeshWeaver provides comprehensive AI capabilities through agents, tools, and natural language interfaces.

---

# Featured Articles

| Article | Description |
|---------|-------------|
| [Agentic AI](MeshWeaver/Documentation/AI/AgenticAI) | Understand the paradigm shift to proactive, goal-oriented AI agents |
| [Vibe Coding](MeshWeaver/Documentation/AI/Vibe Coding) | Can AI build complex business apps? Watch the Mesh Bros put it to the test |

---

# What do you want to do?

| I want to... | Go here |
|--------------|---------|
| Use mesh tools in agents | [MeshPlugin Tools](MeshWeaver/Documentation/AI/Tools/MeshPlugin) - Get, Search, Update, NavigateTo |
| Understand agent architecture | [Agentic AI](MeshWeaver/Documentation/Architecture/AgenticAI) - Multi-agent patterns |
| See AI in action | [ACME Case Studies](ACME/Software/Documentation) - Practical examples |

---

# Core Concepts

## MeshPlugin

The MeshPlugin provides AI agents with tools to interact with the mesh:

| Tool | Purpose |
|------|---------|
| **Get** | Retrieve nodes by path (`@path` or `@path/*` for children) |
| **Search** | Query nodes using GitHub-style syntax |
| **NavigateTo** | Display a node's visual representation |
| **Update** | Create or modify nodes |

[Read more: MeshPlugin Tools](MeshWeaver/Documentation/AI/Tools/MeshPlugin)

---

## Agent Definition

Agents are defined as markdown MeshNodes with `nodeType: Agent`:

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

This declarative approach allows agents to be:
- Versioned alongside application data
- Updated without code changes
- Context-aware through mesh queries

---

## Remote Control Philosophy

AI agents **remote control** applications rather than being embedded:

1. **Separation of Concerns**: Business logic stays independent of AI code
2. **Flexibility**: Agents can be updated without modifying core application
3. **Consistency**: Agents use the same message-based interfaces as users
4. **Security**: Agents are subject to standard access controls

---

# Key Patterns

## Prefer Visual Display

When users ask to "show" or "display" data:

```
User: "Show me the CustomerOnboarding project"
Agent: [Calls NavigateTo('@ACME/CustomerOnboarding')]
       "Here's the CustomerOnboarding project."
```

Use `NavigateTo` instead of returning raw JSON.

## Query Before Action

Before creating or modifying data:

1. Use `Search` to find existing items
2. Use `Get` to retrieve schemas and context
3. Then use `Update` with properly structured data

## Context-Aware Responses

Agents maintain awareness of:
- Current namespace/project context
- Available team members and categories
- Data schemas through GetSchema queries

---

# Agent and Model References

In chat interfaces, you can use unified reference syntax to select agents and models.

## Agent References

Agent references allow you to select a specific AI agent for chat interactions:

```
@agent/AgentName
```

Agents are specialized AI assistants configured for specific tasks or domains. When you mention an agent reference in your message, that agent will handle the conversation.

**Examples:**

```
@agent/Documentation
```

You can combine agent selection with a prompt in the same message:

```
@agent/RiskImportAgent import Microsoft.xlsx
```

Agents can also be selected automatically based on the current navigation context.

---

## Model References

Model references allow you to select a specific AI model for chat interactions:

```
@model/ModelName
```

**Examples:**

```
@model/claude-3-5-sonnet
```

Model names can contain letters, numbers, hyphens, and dots (e.g., `claude-3-5-sonnet`, `gpt-4.0`).

---

## Slash Commands

In addition to @ references, you can use slash commands for agent and model selection:

| Command | Description |
|---------|-------------|
| `/agent AgentName` | Switch to the specified agent |
| `/model ModelName` | Switch to the specified model |
| `/help` | Show available commands |

**Examples:**

```
/agent @agent/RiskImportAgent
/model @model/claude-haiku-4-5
```
