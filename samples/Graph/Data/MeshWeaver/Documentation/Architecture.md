---
Name: Architecture
Category: Documentation
Description: "Overview of MeshWeaver's distributed architecture: message-based communication, UI streaming, AI agents, and data management"
Icon: /static/storage/content/MeshWeaver/Documentation/Architecture/icon.svg
---

# MeshWeaver Architecture

MeshWeaver is a distributed platform for building data-driven applications with AI capabilities. This documentation covers the core architectural concepts.

## Architecture Overview

@@MeshWeaver/Documentation/Architecture/content:platform-overview.svg

## Core Concepts

### 1. Message-Based Communication

**MessageHubs** are the foundation of MeshWeaver. They:
- Manage concurrency through the actor model
- Handle messages for data, layouts, workflows, and more
- Route messages across the mesh
- Support horizontal and vertical cloud-native scaling

[Read more: Message-Based Communication](MeshWeaver/Documentation/Architecture/MessageBasedCommunication)

---

### 2. User Interface

UI is generated **where data lives**:
- Controls defined server-side in a declarative language
- Serialized to JSON and streamed to browsers
- Two-way data binding for real-time updates
- Click events delivered as messages

[Read more: User Interface Architecture](MeshWeaver/Documentation/Architecture/UserInterface)

---

### 3. Agentic AI

AI agents are **first-class citizens** in the mesh:
- Minimal system prompts - agents query for context
- Multi-agent collaboration (Planner, Researcher, Executor)
- MeshPlugin for mesh operations
- MCP integration for external AI services

[Read more: Agentic AI Architecture](MeshWeaver/Documentation/Architecture/AgenticAI)

---

### 4. Mesh Graph

**Data types are data elements**:
- Hierarchical namespaces (a/b/c/d pattern)
- Types attach at any level
- Built-in semantic versioning
- Dynamic hub configuration

[Read more: Mesh Graph Architecture](MeshWeaver/Documentation/Architecture/MeshGraph)

---

### 5. Data Versioning

Technology-specific versioning strategies:
- Snowflake: Time Travel (up to 90 days)
- SQL Server: Temporal tables
- Manual: Path-based versioning (@path@V1, V2)

[Read more: Data Versioning Strategies](MeshWeaver/Documentation/Architecture/DataVersioning)

---

### 6. Access Control

Flexible security through `IDataValidator`:
- Hierarchical: businessArea/department/deal
- Dimensional: geography, line of business
- Operation-specific: read vs. write permissions

[Read more: Access Control Architecture](MeshWeaver/Documentation/Architecture/AccessControl)

---

## Key Principles

| Principle | Description |
|-----------|-------------|
| **Data Locality** | Process and render where data lives |
| **Message-Driven** | All operations as typed messages |
| **Type as Data** | Data types stored in mesh, not code |
| **Agent-Ready** | AI agents access everything through unified APIs |
| **Security-First** | Validation at every operation |

## Getting Started

Explore each architecture topic in depth through the linked articles above, or browse the `MeshWeaver/Documentation/Architecture` namespace.
