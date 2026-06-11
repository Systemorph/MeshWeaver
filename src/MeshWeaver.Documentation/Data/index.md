---
Name: Documentation
Category: Documentation
Description: Your AI-powered data mesh platform — explore the docs, or just ask the assistant
Icon: /static/storage/content/MeshWeaver/logo.svg
---

<div style="background: linear-gradient(135deg, #1e88e5 0%, #6a1b9a 100%); border-radius: 18px; padding: 44px 36px; margin: 4px 0 32px 0; color: #fff;">
  <div style="font-size: 2.3rem; font-weight: 800; letter-spacing: -0.025em; line-height: 1.1;">Welcome to MeshWeaver</div>
  <div style="font-size: 1.1rem; opacity: 0.92; margin-top: 12px; max-width: 720px; line-height: 1.55;">
    Your data, your mesh, your AI. Every piece of data is an addressable node you can query, transform, and collaborate on — with AI agents ready to help at every step.
  </div>
  <div style="margin-top: 18px; font-size: 0.95rem; opacity: 0.8;">New here? Open the chat and ask anything — the assistant knows the platform inside out.</div>
</div>

> **The fastest way to learn MeshWeaver is to ask.** The chat connects you to an AI assistant that understands the entire platform. Try *"What is a data mesh?"*, *"How do node types work?"*, or *"Explain the query syntax."*

---

## Platform at a glance

MeshWeaver is organized around four interconnected pillars. Each section below opens onto its own table of contents — pick a topic that interests you, or scroll down to browse everything.

```mermaid
graph LR
    MW((MeshWeaver))

    MW --> ARCH["Architecture\nMessage-based communication,\nactor model, security"]
    MW --> DM["Data Mesh\nNode types, query syntax,\ncollaborative editing"]
    MW --> GUI["GUI\nControls, layout areas,\ndata binding, observables"]
    MW --> AI["AI Integration\nAgents, tools,\nnatural language"]

    click ARCH "Architecture"
    click DM "DataMesh"
    click GUI "GUI"
    click AI "AI"
```

| Pillar | What you'll find |
|---|---|
| **[Architecture](Architecture)** | Message-based communication, the actor model, access control, and deployment patterns |
| **[Data Mesh](DataMesh)** | Node types, the query syntax, collaborative editing, and data modeling |
| **[GUI](GUI)** | Controls, layout areas, data binding, and reactive observables |
| **[AI Integration](AI)** | Agents, MCP tools, and natural-language access to your mesh |

New to the vocabulary? The **[Glossary](Glossary)** defines every core term in one breath each — mesh node, partition, satellite, hub, stream, and friends.
