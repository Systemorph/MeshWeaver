---
NodeType: "ACME/Article"
Title: "Unified References in Software"
Abstract: "Reference guide for namespace paths, data queries, and layout areas in the Software sample"
Icon: "Document"
Published: "2025-01-31"
Authors:
  - "MeshWeaver Team"
Tags:
  - "ACME"
  - "References"
---

This document demonstrates unified references in the Software sample organization. For the complete Unified Path syntax reference, see [Unified Path](Doc/DataMesh/UnifiedPath).

It covers namespace hierarchy, data queries, content paths, and layout areas specific to the Software sample.

# Organization Structure

The Software sample follows this hierarchy:

```
ACME/                                # Organization
├── Project/                         # Shared NodeType definitions
│   ├── Todo.json                    # Todo NodeType
│   ├── Todo/                        # Todo-related code and assets
│   │   ├── Code/                    # Todo.cs, TodoViews.cs, Status.cs
│   │   └── icon.svg
│   ├── Code/                        # ProjectViews.cs
│   └── TodoAgent.md                 # AI agent
├── CustomerOnboarding/              # Project 1
│   └── Todo/                        # Tasks for onboarding
└── ProductLaunch/                   # Project 2
    └── Todo/                        # Tasks for launch
```

# Namespace Paths

## Organization Level

| Path | Description |
|------|-------------|
| `ACME` | The organization namespace |
| `ACME/Project` | Shared NodeType definitions |

## Project Level

| Path | Description |
|------|-------------|
| `ACME/CustomerOnboarding` | CustomerOnboarding project |
| `ACME/ProductLaunch` | ProductLaunch project |

## NodeType Definitions

| Path | Description |
|------|-------------|
| `ACME/Project/Todo` | Todo NodeType (shared) |
| `ACME/Project/TodoAgent` | AI agent definition |

## Task Instances

**CustomerOnboarding Tasks:**

| Path | Task |
|------|------|
| `ACME/CustomerOnboarding/Todo/ReviewKYC` | Review KYC documentation |
| `ACME/CustomerOnboarding/Todo/CalculateRiskScore` | Calculate risk score |
| `ACME/CustomerOnboarding/Todo/SanctionsScreening` | Sanctions screening |
| `ACME/CustomerOnboarding/Todo/VerifyOwnership` | Verify ownership |
| `ACME/CustomerOnboarding/Todo/CreateClientRecord` | Create client record |

**ProductLaunch Tasks:**

| Path | Task |
|------|------|
| `ACME/ProductLaunch/Todo/PricingStrategy` | Pricing strategy |
| `ACME/ProductLaunch/Todo/EmailCampaign` | Email campaign |
| `ACME/ProductLaunch/Todo/DemoEnvironment` | Demo environment setup |
| `ACME/ProductLaunch/Todo/LandingPageDesign` | Landing page design |
| `ACME/ProductLaunch/Todo/CompetitiveAnalysis` | Competitive analysis |

# Query Syntax

MeshWeaver uses a GitHub-style query syntax for searching nodes. For complete query syntax reference, see [Query Syntax](Doc/DataMesh/QuerySyntax).

## Software Query Examples

**All Tasks in a Project:**

```
path:ACME/CustomerOnboarding/Todo nodeType:ACME/Project/Todo scope:subtree
```

@[ACME/CustomerOnboarding/Todo:nodeType:ACME/Project/Todo:scope:subtree]

**Tasks by Status** (In-progress tasks in ProductLaunch):
```
path:ACME/ProductLaunch/Todo nodeType:ACME/Project/Todo status:InProgress scope:subtree
```

**Tasks by Category** (Legal tasks in CustomerOnboarding):
```
path:ACME/CustomerOnboarding/Todo nodeType:ACME/Project/Todo category:Legal scope:subtree
```

**Tasks by Assignee** (Tasks assigned to Oliver):
```
nodeType:ACME/Project/Todo assignee:Oliver
```

**All Todo Instances** (All tasks across all projects):
```
nodeType:ACME/Project/Todo
```

# Data References

See [Data Prefix](Doc/DataMesh/UnifiedPath/DataPrefix) for the generic data reference syntax.

## Display All Tasks in CustomerOnboarding

```
@ACME/CustomerOnboarding/Todo
```

@ACME/CustomerOnboarding/Todo

## Display Specific Task

```
@ACME/CustomerOnboarding/Todo/ReviewKYC
```

@ACME/CustomerOnboarding/Todo/ReviewKYC

## Display All Tasks in ProductLaunch

```
@ACME/ProductLaunch/Todo
```

@ACME/ProductLaunch/Todo

# Dimension References

Dimensions are shared across all projects using the same NodeType.

## Status Dimension

Defined in `ACME/Project/_Source/Status.cs`:

| Status | Description | Emoji |
|--------|-------------|-------|
| Pending | Task is waiting to be started | ⏳ |
| InProgress | Task is actively being worked on | 🔄 |
| InReview | Task is being reviewed | 👁️ |
| Blocked | Task is blocked by dependencies | 🚫 |
| Completed | Task has been completed | ✅ |

## Priority Dimension

Defined in `ACME/Project/Todo/_Source/Priority.cs`:

| Priority | Order | Color |
|----------|-------|-------|
| Critical | 1 | Red |
| High | 2 | Orange |
| Medium | 3 | (default) |
| Low | 4 | Gray |
| Unset | 5 | (none) |

## Category Dimension

Defined in `ACME/Project/Todo/_Source/Category.cs`:

| Category | Icon |
|----------|------|
| Research | 🔬 |
| Marketing | 📣 |
| Design | 🎨 |
| Sales | 💼 |
| Engineering | ⚙️ |
| PR | 📰 |
| Support | 🎧 |
| Legal | ⚖️ |
| Strategy | 🎯 |
| Partnerships | 🤝 |
| Compliance | 📋 |
| Risk | ⚠️ |
| Operations | 🔧 |
| General | 📁 |

# Content References

## Static Content Paths

| Content | Path |
|---------|------|
| Todo icon | `/static/storage/content/ACME/Project/Todo/icon.svg` |
| Project thumbnails | `/static/storage/content/ACME/Project/thumbnails/` |

## Embedding Todo Icon

```markdown
![Todo Icon](/static/storage/content/ACME/Project/Todo/icon.svg)
```

# Layout Area References

Layout areas are defined in `ProjectViews.cs` and available for all projects. See [Area Prefix](Doc/DataMesh/UnifiedPath/AreaPrefix) for layout area syntax.

## TodaysFocus - Urgent Tasks

Shows overdue, due today, and in-progress tasks:

```
@ACME/CustomerOnboarding/TodaysFocus
```

@ACME/CustomerOnboarding/TodaysFocus

## AllTasks - Tasks by Status

Complete task list grouped by status:

```
@ACME/CustomerOnboarding/AllTasks
```

@ACME/CustomerOnboarding/AllTasks

## TodosByCategory - Tasks by Category

Tasks grouped by category:

```
@ACME/ProductLaunch/TodosByCategory
```

@ACME/ProductLaunch/TodosByCategory

## MyTasks - Current User's Tasks

Tasks assigned to the current user:

```
@ACME/CustomerOnboarding/MyTasks
```

@ACME/CustomerOnboarding/MyTasks

## Backlog - Unassigned Tasks

Unassigned tasks organized by priority:

```
@ACME/ProductLaunch/Backlog
```

@ACME/ProductLaunch/Backlog

# Task Detail Views

Individual task views:

## Details View

The default view showing full task information:

```
@ACME/CustomerOnboarding/Todo/ReviewKYC/Details
```

## Edit View

Opens the task editor:

```
@ACME/CustomerOnboarding/Todo/ReviewKYC/Edit
```

## Thumbnail View

Compact card view for catalog listings:

```
@ACME/CustomerOnboarding/Todo/ReviewKYC/Thumbnail
```

# Navigation Links

## Link to Project

```markdown
[CustomerOnboarding](/ACME/CustomerOnboarding)
[ProductLaunch](/ACME/ProductLaunch)
```

## Link to Specific Task

```markdown
[Review KYC](/ACME/CustomerOnboarding/Todo/ReviewKYC)
[Pricing Strategy](/ACME/ProductLaunch/Todo/PricingStrategy)
```

## Link to Layout Area

```markdown
[Today's Focus](/ACME/CustomerOnboarding/TodaysFocus)
[All Tasks](/ACME/ProductLaunch/AllTasks)
```

# Team Members

## Software Employees

| Name | Role | Typical Categories |
|------|------|-------------------|
| Oliver | Compliance | Legal, Compliance |
| Paul | Risk Management | Risk, Operations |
| Quinn | Customer Support | Support, Operations |

## Platform Team

| Name | Role |
|------|------|
| Alice | Development |
| Bob | Development |
| Carol | Design |
| David | QA |
| Emma | Marketing |
| Roland | Architecture |
| Samuel | DevOps |

# Summary

The Software sample demonstrates MeshWeaver's unified path system:

- **Namespace paths** define the hierarchical structure
- **NodeType references** enable shared definitions across projects
- **Query syntax** allows flexible data retrieval
- **Layout areas** provide consistent views across projects
- **Content paths** reference static assets

All paths follow the pattern: `Organization/Project/[NodeType/][Instance]`
