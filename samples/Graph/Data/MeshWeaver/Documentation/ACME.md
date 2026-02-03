---
Name: ACME Case Studies
Category: Documentation
Description: Learn MeshWeaver through practical examples with the ACME sample organization
Icon: /static/storage/content/MeshWeaver/Documentation/ACME/icon.svg
---

# ACME Case Studies

The ACME sample organization demonstrates MeshWeaver capabilities through realistic business scenarios.

---

## What do you want to learn?

| Topic | Go here |
|-------|---------|
| Get up and running | [Getting Started](MeshWeaver/Documentation/ACME/GettingStarted) - Setup, navigation, first steps |
| Understand the architecture | [Architecture](MeshWeaver/Documentation/ACME/Architecture) - MeshNodes, namespaces, message hubs |
| Add AI to your app | [AI Agent Integration](MeshWeaver/Documentation/ACME/AIAgentIntegration) - Agents, MeshPlugin, NLP |
| Reference paths and queries | [Unified References](MeshWeaver/Documentation/ACME/UnifiedReferences) - Paths, queries, layout areas |

---

## The ACME Organization

ACME is a sample organization with two projects sharing a common Todo application:

```
ACME/                           # Organization
├── Project/                    # Shared NodeTypes
│   ├── Todo.json               # Task NodeType (reusable)
│   └── TodoAgent.md            # AI agent
├── CustomerOnboarding/         # Project 1: Insurance onboarding
│   └── Todo/                   # ReviewKYC, CalculateRiskScore, ...
└── ProductLaunch/              # Project 2: Marketing campaign
    └── Todo/                   # PricingStrategy, EmailCampaign, ...
```

---

## Key Concepts Demonstrated

### Namespace Hierarchy

Data is organized in a hierarchical namespace:
- **Organization** → **Project** → **Task**
- Each level has its own context and permissions
- Shared NodeTypes enable code reuse across projects

### NodeType Reuse

The Todo NodeType (`ACME/Project/Todo`) is defined once and used by both projects:
- Same data model, views, and behavior
- Project-specific instances with relevant categories
- Shared AI agent for task management

### AI Agent Integration

The TodoAgent demonstrates:
- Natural language task creation and queries
- Team member and category awareness
- Layout area integration for visual responses

---

## Sample Data

### CustomerOnboarding Project

Insurance client onboarding with compliance-focused tasks:

| Task | Assignee | Category |
|------|----------|----------|
| Review KYC documentation | Oliver | Legal |
| Calculate risk score | Paul | Risk |
| Sanctions screening | Oliver | Compliance |

### ProductLaunch Project

Marketing campaign with cross-functional tasks:

| Task | Assignee | Category |
|------|----------|----------|
| Pricing strategy | Alice | Strategy |
| Email campaign | Emma | Marketing |
| Demo environment | Bob | Engineering |

---

## Explore Further

Navigate to `ACME` in the portal to explore:
- Both projects with their tasks
- Different views: TodaysFocus, AllTasks, TodosByCategory
- The AI chat agent for natural language interactions
