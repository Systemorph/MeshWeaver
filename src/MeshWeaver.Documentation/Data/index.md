---
Name: Documentation
Category: Documentation
Description: Your AI-powered data mesh platform — explore, ask, build
Icon: /static/storage/content/MeshWeaver/logo.svg
---

# Welcome to MeshWeaver

**Your data, your mesh, your AI.** MeshWeaver is a distributed data mesh platform where every piece of data is a node you can query, transform, and collaborate on — with AI agents ready to help at every step.

> **New here?** Just open the chat below and ask anything. Our AI assistant knows the platform inside out and will guide you through whatever you need.

---

## Platform Overview

```mermaid
graph LR
    classDef arch fill:#1565c0,stroke:#0d47a1,color:#fff,rx:8,ry:8
    classDef data fill:#2e7d32,stroke:#1b5e20,color:#fff,rx:8,ry:8
    classDef gui fill:#6a1b9a,stroke:#4a148c,color:#fff,rx:8,ry:8
    classDef ai fill:#e65100,stroke:#bf360c,color:#fff,rx:8,ry:8
    classDef core fill:#37474f,stroke:#263238,color:#fff,rx:12,ry:12

    MW((MeshWeaver)):::core

    MW --> ARCH["Architecture\nMessage-based communication,\nactor model, security"]:::arch
    MW --> DM["Data Mesh\nNode types, query syntax,\ncollaborative editing"]:::data
    MW --> GUI["GUI\nControls, layout areas,\ndata binding, observables"]:::gui
    MW --> AI["AI Integration\nAgents, tools,\nnatural language"]:::ai

    click ARCH "/Doc/Architecture"
    click DM "/Doc/DataMesh"
    click GUI "/Doc/GUI"
    click AI "/Doc/AI"
```

---

## Explore by Topic

### [Architecture](/Doc/Architecture)
The backbone of MeshWeaver: message-based communication, actor model, partitioned persistence, access control, and UI streaming. Start here to understand how the platform works under the hood.

### [Data Mesh](/Doc/DataMesh)
Everything about nodes: node types, query syntax, unified content references, interactive markdown, collaborative editing, and CRUD operations. The data layer that powers all of MeshWeaver.

### [GUI](/Doc/GUI)
Build reactive UIs from C# code: editors, data grids, layout areas, data binding, observables, container controls, and attributes. No frontend framework required.

### [AI Integration](/Doc/AI)
MeshPlugin tools, agent definitions, model selection, remote control, and natural language interfaces. Let AI agents work alongside your data mesh.

---

## Get Started in Seconds

You don't need to read pages of documentation. Just **ask**. Here are some things you can try:

- *"What is a data mesh?"*
- *"Show me how node types work"*
- *"How do I create a custom node type?"*
- *"Explain the query syntax"*
- *"How does access control work?"*

---

## Ask Anything

Don't search — just ask. The chat below connects you to an AI assistant that understands the entire MeshWeaver platform.
