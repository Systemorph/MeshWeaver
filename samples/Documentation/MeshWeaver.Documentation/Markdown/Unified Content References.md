---
Title: "Unified Content References"
Abstract: >
  MeshWeaver introduces a unified notation for referencing any form of downloadable content.
  This includes data entities, file content, and layout areas - all accessible through a consistent
  path-based syntax that can be embedded directly in markdown documents.
Thumbnail: "images/UnifiedReferences.svg"
Published: "2025-12-06"
Authors:
  - "Roland Bürgi"
Tags:
  - "Documentation"
  - "Conceptual"
  - "Markdown"
  - "Data"
  - "Content"
---

MeshWeaver provides a unified notation for referencing any form of content. Whether you need to embed data, include file content, display layout areas, or select AI agents and models, the syntax follows a consistent pattern:

```
@addressType/addressId[/keyword[/path]]
```

The keyword determines how the content is fetched and rendered:
- `data` - Fetches data entities and displays them as JSON
- `content` - Fetches file content and renders based on mime type
- `area` - Displays a layout area (this is the default if no keyword is specified)

Special prefixes for AI interactions:
- `agent/` - Selects an AI agent for chat interactions
- `model/` - Selects an AI model for chat interactions

For paths containing spaces, use quotes: `@"app/Docs/content/My Report.pdf"`

## Content References

Content references embed file content directly in your markdown. The content is rendered based on its mime type. The format is:

```
@addressType/addressId/content/collection/path
```

### Example: Embedding an Image

To display an image from the Documentation collection:

```
@app/Documentation/content/Documentation/images/meshbros.png
```

@app/Documentation/content/Documentation/images/meshbros.png

### Example: Embedding a Markdown Document

To include content from another markdown file:

```
@app/Documentation/content/Documentation/embedded.md
```

@app/Documentation/content/Documentation/embedded.md

## Layout Area References

Layout areas display interactive components. The format is:

```
@addressType/addressId/areaName
```

Since `area` is the default keyword, you don't need to specify it explicitly.

### Example: Embedding the Calculator

The Calculator layout area demonstrates a simple interactive component:

```
@app/Documentation/Calculator
```

@app/Documentation/Calculator

### Example: Embedding the Counter

The Counter layout area demonstrates stateful views with click actions:

```
@app/Documentation/Counter
```

@app/Documentation/Counter

### Example: Embedding Progress Indicators

The Progress layout area demonstrates progress bars:

```
@app/Documentation/Progress
```

@app/Documentation/Progress

## Agent References

Agent references allow you to select a specific AI agent for chat interactions. The format is:

```
@agent/AgentName
```

Agents are specialized AI assistants configured for specific tasks or domains. When you mention an agent reference in your message, that agent will handle the conversation.

### Example: Selecting an Agent

To select the Documentation agent:

```
@agent/Documentation
```

You can combine agent selection with a prompt in the same message:

```
@agent/RiskImportAgent import Microsoft.xlsx
```

Agents can also be selected automatically based on the current navigation context. When you navigate to different areas of the application, the most appropriate agent for that context is automatically selected.

## Model References

Model references allow you to select a specific AI model for chat interactions. The format is:

```
@model/ModelName
```

### Example: Selecting a Model

To select a specific model:

```
@model/claude-3-5-sonnet
```

Model names can contain letters, numbers, hyphens, and dots (e.g., `claude-3-5-sonnet`, `gpt-4.0`).

## Slash Commands

In addition to @ references, you can use slash commands for agent and model selection:

- `/agent AgentName` - Switch to the specified agent
- `/model ModelName` - Switch to the specified model
- `/help` - Show available commands

### Examples

Switch to the RiskImport agent using a slash command:

```
/agent @agent/RiskImportAgent
```

Switch to a specific model:

```
/model @model/claude-haiku-4-5
```
