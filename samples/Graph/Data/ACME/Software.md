---
NodeType: Organization
Name: ACME Software
Category: Task Management
Description: Project and task management demo showcasing MeshWeaver's collaborative workflows and AI agent integration
Thumbnail: Software/icon.svg
---

Welcome to ACME Software, a task management demo showcasing MeshWeaver's collaborative project workflows. Explore how organizations can manage projects with nested tasks, team assignments, and AI-assisted task handling.

# Quick Start

| Resource | Description |
|----------|-------------|
| [CustomerOnboarding](ACME/Software/CustomerOnboarding) | Insurance onboarding workflow with compliance tasks |
| [ProductLaunch](ACME/Software/ProductLaunch) | Marketing campaign with cross-functional tasks |
| [Getting Started](ACME/Software/Documentation/GettingStarted) | Setup and first steps |
| [Documentation](ACME/Software/Documentation) | Complete guides and references |

---

# What's Inside

## Organization Structure

ACME demonstrates a hierarchical namespace pattern:

```
ACME (Organization)
  └── Project (NodeType)
        └── Todo (NodeType)
```

**NodeType Reuse**: The `Project` and `Todo` types are defined once in the `ACME/Software` namespace and reused across multiple project instances.

---

## Sample Projects

| Project | Focus | Tasks |
|---------|-------|-------|
| [CustomerOnboarding](ACME/Software/CustomerOnboarding) | Insurance compliance | KYC review, risk scoring, policy generation |
| [ProductLaunch](ACME/Software/ProductLaunch) | Marketing campaign | Landing pages, demos, sales training |

---

## Key Features Demonstrated

**AI Agent Integration**
Each project includes a `TodoAgent` that can assist with task management, status updates, and workflow automation through natural language.

**Namespace Hierarchy**
Types defined at `ACME/Software/Project` are inherited by all project instances, enabling consistent behavior while allowing per-project customization.

**Real-Time Collaboration**
Tasks support assignees, due dates, priorities, and status tracking with reactive updates across views.

---

# Project Views

Projects include multiple perspectives for task management:

| View | Purpose |
|------|---------|
| TodaysFocus | Tasks due today or overdue |
| AllTasks | Complete task list with filters |
| TodosByCategory | Tasks grouped by category |
| MyTasks | Tasks assigned to current user |
| Backlog | Tasks in backlog status |

---

# Team Members

| Member | Role |
|--------|------|
| Oliver | Project management |
| Paul | Technical lead |
| Quinn | QA and compliance |
| Alice | Marketing |
| Bob | Development |
| Carol | Design |
| David | Sales |
| Emma | Operations |

---

# Learn More

| Topic | Link |
|-------|------|
| Architecture | [How ACME is built](ACME/Software/Documentation/Architecture) |
| AI Integration | [Using the TodoAgent](ACME/Software/Documentation/AIAgentIntegration) |
| References | [Paths, queries, and areas](ACME/Software/Documentation/UnifiedReferences) |
