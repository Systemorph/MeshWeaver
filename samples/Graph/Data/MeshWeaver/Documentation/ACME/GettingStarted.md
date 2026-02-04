---
Name: Getting Started with ACME
Category: Case Studies
Description: Explore the ACME sample organization and learn MeshWeaver fundamentals through practical examples
Icon: /static/storage/content/MeshWeaver/Documentation/ACME/icon.svg
---

# Getting Started with MeshWeaver

The ACME sample organization demonstrates MeshWeaver's capabilities through a realistic business scenario: an organization with multiple projects, each using a shared Todo application for task management.

## The ACME Sample Organization

ACME demonstrates how MeshWeaver organizes data and applications:

### Organization Structure

```
ACME/                                    # Organization level
├── Project/                             # Shared NodeType definitions
│   ├── Todo.json                        # Task NodeType (reusable)
│   ├── Todo/Code/                       # Todo.cs, TodoViews.cs, Status.cs, etc.
│   ├── Code/                            # ProjectViews.cs (project-level views)
│   └── TodoAgent.md                     # AI agent for task management
├── CustomerOnboarding/                  # Project: Insurance onboarding
│   └── Todo/                            # Tasks: ReviewKYC, CalculateRiskScore, etc.
└── ProductLaunch/                       # Project: Marketing campaign
    └── Todo/                            # Tasks: PricingStrategy, EmailCampaign, etc.
```

### Key Concepts Demonstrated

1. **Organization → Project → Task Hierarchy**: ACME shows how namespaces create natural data partitioning
2. **Shared NodeTypes**: The Todo NodeType (`ACME/Project/Todo`) is reused across multiple projects
3. **Domain-Specific Data**: Each project has its own tasks with appropriate categories
4. **AI Agent Integration**: The TodoAgent helps manage tasks across all projects

### Sample Projects

**CustomerOnboarding** - Insurance client onboarding workflow:
- Tasks: Review KYC, Calculate Risk Score, Sanctions Screening, Verify Ownership
- Team: Oliver (Compliance), Paul (Risk Management), Quinn (Customer Support)
- Categories: Legal, Compliance, Risk, Operations

**ProductLaunch** - Marketing campaign management:
- Tasks: Pricing Strategy, Email Campaign, Demo Environment, Landing Page Design
- Team: Alice, Bob, Carol, David, Emma
- Categories: Marketing, Engineering, Design, Sales, PR

## Running the Sample

### Clone and Build

```bash
git clone https://github.com/MeshWeaver/MeshWeaver.git
cd MeshWeaver
dotnet build
```

### Start the Portal

```bash
cd loom/Loom.Portal.Monolith
dotnet run
```

Navigate to `http://localhost:7122` in your browser.

### Navigate to ACME

1. Navigate to **ACME** organization
2. Explore **CustomerOnboarding** or **ProductLaunch** projects
3. View tasks using different views: TodaysFocus, AllTasks, TodosByCategory

## Setting up AI Integration

MeshWeaver supports multiple AI providers for the chat agent functionality. To enable AI features, you'll need to configure an API key for your chosen provider.

## Exploring the Todo Interface

### Project Views

Each project (CustomerOnboarding, ProductLaunch) has these views:

| View | Description |
|------|-------------|
| **TodaysFocus** | Urgent tasks: overdue, due today, in progress |
| **AllTasks** | All tasks grouped by status (Planning, Active, Completed, etc.) |
| **TodosByCategory** | Tasks grouped by category (Legal, Marketing, Engineering, etc.) |
| **MyTasks** | Your assigned tasks grouped by urgency |
| **Backlog** | Unassigned tasks organized by priority |

### Task Card Features

Each task card shows:
- **Title** with link to details
- **Priority badge** (Critical, High, Low)
- **Category** and **due date**
- **Status actions** (Start, Complete, etc.)
- **Edit** and **Delete** options

### Task Details View

Clicking a task opens the full details:
- Header with status icon, title, priority, and status badges
- Properties grid: Category, Priority, Assignee, Due Date, Created
- Description section with markdown support
- Status promotion menu for quick transitions
- Action menu: Edit, Delete, Comments, Files, Metadata, Settings

## Using the Todo Agent

The AI chat agent understands natural language and can manage tasks across projects.

### Creating Tasks

```
"I need to create a compliance review for the new client"
→ Creates task in CustomerOnboarding with Legal category

"Add a task to update the demo environment before launch"
→ Creates task in ProductLaunch with Engineering category
```

### Querying Tasks

```
"Show me all overdue tasks"
→ Displays TodaysFocus view highlighting overdue items

"What Marketing tasks are in ProductLaunch?"
→ Shows TodosByCategory filtered to Marketing

"Show Oliver's tasks"
→ Displays tasks assigned to Oliver
```

### Modifying Tasks

```
"Mark the KYC review as complete"
→ Updates status to Completed

"Move the pricing strategy task to next week"
→ Updates due date

"Assign the email campaign to Emma"
→ Updates assignee
```

### Context-Aware Operations

The agent understands context:
- **Project context**: Knows which project you're viewing
- **Team members**: Recognizes Oliver, Paul, Quinn (ACME) and Alice, Bob, etc. (platform)
- **Categories**: Maps descriptions to appropriate categories
- **Relative dates**: Converts "tomorrow", "next Friday" to specific dates

## Understanding the Code

### Todo Data Model (`Todo.cs`)

```csharp
public record Todo : IContentInitializable
{
    [Key]
    public string Id { get; init; } = string.Empty;

    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Title { get; init; } = string.Empty;

    [Markdown]
    public string? Description { get; init; }

    [Dimension<Category>]
    public string Category { get; init; } = "General";

    [Dimension<Priority>]
    public string Priority { get; init; } = "Medium";

    public string? Assignee { get; init; }

    [Dimension<Status>]
    public string Status { get; init; } = "Planning";

    public DateTime? DueDate { get; init; }
}
```

Key features:
- `[Key]` marks the primary identifier
- `[MeshNodeProperty]` maps to MeshNode properties
- `[Dimension<T>]` links to dimension types for filtering
- `[Markdown]` enables markdown rendering for descriptions

### NodeType Configuration (`Todo.json`)

```json
{
  "id": "Todo",
  "namespace": "ACME/Project",
  "name": "Task",
  "nodeType": "NodeType",
  "content": {
    "$type": "NodeTypeDefinition",
    "configuration": "config => config
        .WithContentType<Todo>()
        .AddData(data => data.AddHubSource(...))
        .AddDefaultLayoutAreas()
        .AddLayout(layout => layout.WithView(\"Thumbnail\", TodoViews.Thumbnail))"
  }
}
```

### Task Instance (`ReviewKYC.json`)

```json
{
  "id": "ReviewKYC",
  "namespace": "ACME/CustomerOnboarding/Todo",
  "name": "Review KYC documentation",
  "nodeType": "ACME/Project/Todo",
  "content": {
    "$type": "Todo",
    "id": "ReviewKYC",
    "title": "Review KYC documentation",
    "description": "Review all submitted KYC documents...",
    "category": "Legal",
    "priority": "Critical",
    "assignee": "Oliver",
    "status": "InProgress"
  }
}
```

## Architecture Deep Dive

For detailed understanding of MeshWeaver's architecture in the context of the ACME Sample Organization:

- **[Understanding MeshWeaver Architecture](MeshWeaver/Documentation/ACME/Architecture)**: Message hubs, MVVM patterns, reactive design
- **[AI Agent Integration](MeshWeaver/Documentation/ACME/AIAgentIntegration)**: How AI agents work with MeshWeaver
- **[Unified References](MeshWeaver/Documentation/ACME/UnifiedReferences)**: Path syntax and reference patterns

## Next Steps

1. **Explore the Data**: Navigate through ACME projects and examine task data
2. **Use the Agent**: Try natural language commands in the chat interface
3. **Modify Tasks**: Create, edit, and update tasks to see real-time updates
4. **Examine the Code**: Review `Todo.cs`, `TodoViews.cs`, `ProjectViews.cs`
5. **Create Your Own**: Add new tasks or even new projects following the patterns

The ACME sample provides a complete working example of MeshWeaver's capabilities, from data modeling and views to AI agent integration and real-time collaboration.
