---
Name: Creating Node Types
Category: Documentation
Description: Step-by-step guide to defining custom node types with C# content models, reference data, layout areas, and CSV data loading
Icon: Code
---

# Creating Node Types

MeshWeaver's NodeType system lets you define richly typed data models — complete with custom UI, reference lookups, and CSV-backed data — entirely from source files that live alongside your data. This guide walks you through building a complete NodeType from scratch.

---

## How a NodeType Is Structured

Every NodeType lives in its own namespace. Source code goes in the `Source/` namespace and xUnit tests in `Test/`. The JSON definition file sits at the same level as the namespace it describes:

<svg viewBox="0 0 760 300" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#90a4ae"/>
    </marker>
  </defs>
  <rect x="20" y="20" width="160" height="260" rx="10" fill="#263238" stroke="#546e7a" stroke-width="1.5"/>
  <text x="100" y="46" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#eceff1">Source/ Namespace</text>
  <rect x="34" y="58" width="132" height="30" rx="6" fill="#1e88e5"/>
  <text x="100" y="78" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">Content Record</text>
  <rect x="34" y="98" width="132" height="30" rx="6" fill="#43a047"/>
  <text x="100" y="118" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">Reference Data</text>
  <rect x="34" y="138" width="132" height="30" rx="6" fill="#f57c00"/>
  <text x="100" y="158" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">Layout Areas</text>
  <rect x="34" y="178" width="132" height="30" rx="6" fill="#8e24aa"/>
  <text x="100" y="198" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">CSV Data Loader</text>
  <rect x="34" y="218" width="132" height="30" rx="6" fill="#546e7a"/>
  <text x="100" y="238" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">Unit Tests</text>
  <rect x="290" y="80" width="170" height="60" rx="10" fill="#37474f" stroke="#78909c" stroke-width="1.5"/>
  <text x="375" y="106" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#eceff1">NodeType JSON</text>
  <text x="375" y="126" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#b0bec5">Project.json</text>
  <rect x="540" y="40" width="185" height="220" rx="10" fill="#1b2838" stroke="#546e7a" stroke-width="1.5"/>
  <text x="632" y="68" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#eceff1">Runtime NodeType</text>
  <rect x="555" y="78" width="155" height="26" rx="6" fill="#1e88e5"/>
  <text x="632" y="96" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">WithContentType&lt;T&gt;()</text>
  <rect x="555" y="112" width="155" height="26" rx="6" fill="#43a047"/>
  <text x="632" y="130" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">AddData() + WithType&lt;T&gt;()</text>
  <rect x="555" y="146" width="155" height="26" rx="6" fill="#f57c00"/>
  <text x="632" y="164" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">AddLayout() + Views</text>
  <rect x="555" y="180" width="155" height="26" rx="6" fill="#8e24aa"/>
  <text x="632" y="198" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">WithInitialData(loader)</text>
  <rect x="555" y="214" width="155" height="26" rx="6" fill="#26a69a"/>
  <text x="632" y="232" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#fff">AddHubSource() (children)</text>
  <line x1="180" y1="110" x2="285" y2="110" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="464" y1="110" x2="535" y2="110" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <text x="220" y="103" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#b0bec5" fill-opacity="0.8">defines</text>
  <text x="498" y="103" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#b0bec5" fill-opacity="0.8">wires</text>
</svg>

*Source files in `Source/` are compiled at startup; the JSON definition wires them into the running NodeType configuration.*

```
samples/Graph/Data/
  ACME/
    Project.json              # NodeType definition (nodeType: "NodeType")
    Project/
      Source/                 # C# code compiled at startup
        Project.cs            # Content record
        Status.cs             # Reference data type
        Category.cs           # Reference data type
        Priority.cs           # Reference data type
        ProjectLayoutAreas.cs # Custom layout areas
      Test/                   # xUnit tests
        ProjectTests.cs
      Todo.json               # Child NodeType definition
      Todo/
        Source/
          Todo.cs             # Child content record
          TodoLayoutAreas.cs  # Child layout areas
```

> **Key idea**: the `Source/` namespace is compiled at startup. Every `.cs` Code node you put there becomes live code — content types, dimension types, layout areas, and data loaders all coexist in this single namespace.

---

## Step 1: Define the Content Type

The content type is a C# `record` that describes the fields of one node instance. Place it in `Source/` with a `<meshweaver>` frontmatter comment so the compiler knows its identity:

```csharp
// <meshweaver>
// Id: Project
// DisplayName: Project Data Model
// </meshweaver>

using MeshWeaver.Domain;

public record Project
{
    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public ProjectStatus Status { get; init; } = ProjectStatus.Active;

    [MeshNodeProperty(nameof(MeshNode.Icon))]
    public string Icon { get; init; } = "Folder";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? TargetDate { get; init; }
}

public enum ProjectStatus
{
    Planning, Active, OnHold, Completed, Cancelled
}
```

### Available Attributes

| Attribute | Purpose |
|-----------|---------|
| `[Key]` | Primary identifier field |
| `[Required]` | Validation — field must be set |
| `[MeshNodeProperty(nameof(MeshNode.Name))]` | Maps the field to the MeshNode's `Name` property |
| `[MeshNodeProperty(nameof(MeshNode.Icon))]` | Maps the field to the MeshNode's `Icon` property |
| `[MeshNode("nodeType:ACME/Category")]` | References another mesh node — renders a `MeshNodePicker`, stores the node's PATH. The query always uses the **full path of the referenced NodeType** (see [Data Cubes](/Doc/DataMesh/DataCubes)) |
| `[Dimension<Category>]` | References an in-hub lookup / dimension type seeded via `WithType<T>(t => t.WithInitialData(...))` |
| `[Markdown(EditorHeight = "200px")]` | Renders a rich text editor for this field |
| `[UiControl(Style = "width: 200px;")]` | Controls form layout width |
| `[Browsable(false)]` | Hides the field from all UI |
| `[DisplayName("Due Date")]` | Custom label in generated forms |

### Content Type with Dimensions

When a child type needs to reference lookup data from its parent, implement `IContentInitializable` to resolve dynamic defaults at creation time:

```csharp
// <meshweaver>
// Id: Todo
// DisplayName: Todo Data Model
// </meshweaver>

using MeshWeaver.Domain;

public record Todo : IContentInitializable
{
    [Required]
    [UiControl(Style = "width: 100%;")]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Title { get; init; } = string.Empty;

    [Markdown(EditorHeight = "200px", ShowPreview = false)]
    public string? Description { get; init; }

    [Dimension<Category>]
    [UiControl(Style = "width: 200px;")]
    public string Category { get; init; } = "General";

    [Dimension<Priority>]
    [UiControl(Style = "width: 150px;")]
    public string Priority { get; init; } = "Medium";

    [Dimension<Status>]
    [UiControl(Style = "width: 150px;")]
    public string Status { get; init; } = "Pending";

    public string? Assignee { get; init; }

    public DateTime? DueDate { get; init; }

    [Browsable(false)]
    public int? DueDateOffsetDays { get; init; }

    public object Initialize()
    {
        if (DueDateOffsetDays.HasValue)
            return this with { DueDate = DateTime.UtcNow.Date.AddDays(DueDateOffsetDays.Value) };
        return this;
    }
}
```

---

## Step 2: Define Reference Data Types

Reference data types supply the dropdown values for `[Dimension<T>]` fields. They follow the same `<meshweaver>` frontmatter convention and expose a static `All` array so the NodeType configuration can seed them at startup:

```csharp
// <meshweaver>
// Id: Status
// DisplayName: Project Status Data Model
// </meshweaver>

public record Status
{
    [Key]
    public string Id { get; init; } = string.Empty;

    [Required]
    public string Name { get; init; } = string.Empty;

    public string Emoji { get; init; } = string.Empty;

    public int Order { get; init; }

    public bool IsExpandedByDefault { get; init; } = true;

    public static readonly Status Pending = new()
    {
        Id = "Pending", Name = "Pending", Emoji = "⏳", Order = 0
    };

    public static readonly Status InProgress = new()
    {
        Id = "InProgress", Name = "In Progress", Emoji = "🔄", Order = 1
    };

    public static readonly Status Completed = new()
    {
        Id = "Completed", Name = "Completed", Emoji = "✅", Order = 4,
        IsExpandedByDefault = false
    };

    public static readonly Status[] All = [Pending, InProgress, Completed];

    public static Status GetById(string? id) =>
        All.FirstOrDefault(s => s.Id == id) ?? Pending;
}
```

---

## Step 3: Create the NodeType Definition (JSON)

The JSON file in the parent namespace wires everything together. The `configuration` field holds a C# lambda expression that is compiled and executed at startup:

```json
{
  "id": "Project",
  "namespace": "ACME",
  "name": "Project",
  "nodeType": "NodeType",
  "category": "Types",
  "description": "A project containing tasks and deliverables",
  "icon": "/static/storage/content/ACME/Project/icon.svg",
  "isPersistent": true,
  "content": {
    "$type": "NodeTypeDefinition",
    "namespace": "ACME",
    "displayName": "Project",
    "description": "A project containing tasks and deliverables",
    "configuration": "config => config
      .WithContentType<Project>()
      .AddData(data => data
        .AddSource(source => source
          .WithType<Status>(t => t.WithInitialData(Status.All))
          .WithType<Category>(t => t.WithInitialData(Category.All))
          .WithType<Priority>(t => t.WithInitialData(Priority.All))))
      .AddLayout(layout => layout
        .AddLayoutAreaCatalog()
        .AddProjectLayoutAreas()
        .WithDefaultArea(\"LayoutAreas\"))"
  }
}
```

### What each builder call does

| Builder call | Purpose |
|---|---|
| `WithContentType<T>()` | Registers the content record for the editor form |
| `AddData(...)` | Configures the `MeshDataSource` with reference data and virtual types |
| `AddSource(source => source.WithType<T>(...))` | Registers types in the data source |
| `WithInitialData(T[] items)` | Seeds reference data from a static array |
| `WithInitialData(Func<CancellationToken, Task<IEnumerable<T>>>)` | Seeds data from an async loader (e.g., CSV) |
| `AddLayout(...)` | Configures views and layout areas |

---

## Step 4: Loading Data from CSV Files

When data comes from CSV files rather than static arrays — as in the Northwind sample — define a loader in `Source/` and wire it up with `WithInitialData`.

### Define the Type

```csharp
// <meshweaver>
// Id: Product
// DisplayName: Product
// </meshweaver>

using MeshWeaver.Domain;

public record Product : INamed
{
    [Key]
    public int ProductId { get; init; }

    public string ProductName { get; init; } = string.Empty;

    [Dimension(typeof(Supplier))]
    public int SupplierId { get; init; }

    [Dimension(typeof(Category))]
    public int CategoryId { get; init; }

    public double UnitPrice { get; init; }

    public short UnitsInStock { get; init; }

    string INamed.DisplayName => ProductName;
}
```

### Create the CSV Loader

```csharp
// <meshweaver>
// Id: DataLoader
// DisplayName: Data Loader
// </meshweaver>

using System.Globalization;

public static class DataLoader
{
    private static readonly string BasePath =
        Path.Combine("../../samples/Graph/attachments/MyData");

    public static Task<IEnumerable<Product>> LoadProductsAsync(CancellationToken ct)
    {
        var lines = File.ReadAllLines(Path.Combine(BasePath, "products.csv"));
        return Task.FromResult(ParseCsv(lines, parts => new Product
        {
            ProductId = int.Parse(parts[0]),
            ProductName = parts[1],
            SupplierId = int.Parse(parts[2]),
            CategoryId = int.Parse(parts[3]),
            UnitPrice = double.Parse(parts[4], CultureInfo.InvariantCulture),
            UnitsInStock = short.Parse(parts[5]),
        }));
    }

    private static IEnumerable<T> ParseCsv<T>(
        string[] lines, Func<string[], T> factory)
    {
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            yield return factory(line.Split(','));
        }
    }
}
```

### Wire Up in the NodeType Configuration

```json
{
  "content": {
    "$type": "NodeTypeDefinition",
    "configuration": "config => config
      .WithContentType<CatalogContent>()
      .AddContentCollection(sp => new ContentCollectionConfig {
        SourceType = FileSystemStreamProvider.SourceType,
        Name = \"Data\",
        BasePath = \"../../samples/Graph/attachments/MyData\",
        DisplayName = \"Data Files\"
      })
      .AddData(data => data
        .AddSource(source => source
          .WithType<Category>(t => t.WithInitialData(DataLoader.LoadCategoriesAsync))
          .WithType<Product>(t => t.WithInitialData(DataLoader.LoadProductsAsync))
          .WithType<Order>(t => t.WithInitialData(DataLoader.LoadOrdersAsync))))
      .AddDefaultLayoutAreas()
      .AddLayout(layout => layout.WithDefaultArea(\"LayoutAreas\"))"
  }
}
```

> **Key points for CSV data:**
> - Place CSV files in an `attachments/` folder and reference them via `AddContentCollection`.
> - The loader reads the CSV, skips the header row, and maps columns to record properties.
> - Use `WithInitialData(Func<CancellationToken, Task<IEnumerable<T>>>)` for async CSV loading.
> - Use `WithInitialData(T[])` for static in-memory reference data.
> - `[Dimension(typeof(T))]` declares a relationship between types so the query engine can perform join operations.
> - Implement `INamed` to provide a display name for lookup columns in the UI.

---

## Step 5: Create Layout Areas

Layout areas define what users see when they open a node. Register them as extension methods on `MessageHubConfiguration` so the NodeType configuration lambda can call `AddProjectLayoutAreas()`:

```csharp
// <meshweaver>
// Id: ProjectLayoutAreas
// DisplayName: Project Layout Areas
// </meshweaver>

using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

public static class ProjectLayoutAreas
{
    public static MessageHubConfiguration AddProjectLayoutAreas(
        this MessageHubConfiguration config)
        => config.AddLayout(layout => layout
            .WithView("Dashboard", Dashboard));

    public static IObservable<UiControl?> Dashboard(
        LayoutAreaHost host, RenderingContext _)
    {
        return host.Workspace.GetObservable<Status>()
            .CombineLatest(
                host.Workspace.GetObservable<Todo>(),
                (statuses, todos) =>
                {
                    // Build view from live data
                    return Controls.Stack
                        .WithView(Controls.Html("<h2>Dashboard</h2>"));
                });
    }
}
```

---

## Child NodeType Definitions

Child types are defined in subfolders and follow the same pattern. The key difference is `AddHubSource`, which imports reference types from the parent node's data source — so child instances automatically share the parent's lookup data without re-declaring it:

```json
{
  "id": "Todo",
  "namespace": "ACME/Project",
  "name": "Task",
  "nodeType": "NodeType",
  "content": {
    "$type": "NodeTypeDefinition",
    "namespace": "ACME/Project",
    "displayName": "Task",
    "configuration": "config => config
      .WithContentType<Todo>()
      .AddData(data => data
        .AddHubSource(
          new Address(config.Address.Segments.Take(
            config.Address.Segments.Length - 2).ToArray()),
          source => source
            .WithType<Status>()
            .WithType<Category>()
            .WithType<Priority>()))
      .AddDefaultLayoutAreas()"
  }
}
```

`AddHubSource(parentAddress, ...)` opens a live subscription to the parent node's data source, so any updates to the parent's reference data are immediately visible to child instances.

---

## Live Example: Attribute Reference

The table below summarises which attribute to reach for at each stage of model definition. It is rendered live from a small in-kernel data table:

```csharp --render NodeTypeAttributeRef --show-code
var rows = new[]
{
    ("[Key]",                                       "Record identity", "Primary key field for dimension types"),
    ("[Required]",                                  "Validation",      "Field must be non-null / non-empty"),
    ("[MeshNodeProperty(nameof(MeshNode.Name))]",   "Node mapping",    "Binds the field to the MeshNode Name shown in navigation"),
    ("[MeshNodeProperty(nameof(MeshNode.Icon))]",   "Node mapping",    "Binds the field to the MeshNode Icon"),
    ("[Dimension&lt;T&gt;]",                        "Relationships",   "Declares a lookup relationship to dimension type T"),
    ("[Dimension(typeof(T))]",                      "Relationships",   "Alternative syntax for non-generic dimension reference"),
    ("[Markdown(...)]",                             "Editor control",  "Renders a rich Markdown editor for the field"),
    ("[UiControl(Style = &quot;...&quot;)]",        "Layout",          "Applies inline CSS to the form control"),
    ("[Browsable(false)]",                          "Visibility",      "Excludes the field from all generated UI"),
    ("[DisplayName(&quot;...&quot;)]",              "Labels",          "Custom label in generated forms"),
};

var bodyRows = string.Join("", rows.Select((r, i) =>
{
    var bg = i % 2 == 0 ? "#f9f9f9" : "white";
    return $"<tr style='background:{bg}'>" +
           $"<td style='padding:5px 10px;font-family:monospace;font-size:0.85em'>{r.Item1}</td>" +
           $"<td style='padding:5px 10px'>{r.Item2}</td>" +
           $"<td style='padding:5px 10px'>{r.Item3}</td>" +
           "</tr>";
}));

MeshWeaver.Layout.Controls.Stack
    .WithView(MeshWeaver.Layout.Controls.Markdown("### Content Type Attribute Reference"))
    .WithView(MeshWeaver.Layout.Controls.Html(
        "<table style='width:100%;border-collapse:collapse'>" +
        "<thead><tr>" +
        "<th style='text-align:left;padding:6px 10px;border-bottom:2px solid #ccc'>Attribute</th>" +
        "<th style='text-align:left;padding:6px 10px;border-bottom:2px solid #ccc'>Category</th>" +
        "<th style='text-align:left;padding:6px 10px;border-bottom:2px solid #ccc'>Effect</th>" +
        "</tr></thead><tbody>" +
        bodyRows +
        "</tbody></table>"
    ))
```

---

## Summary

Here is the complete checklist for a new NodeType:

| Step | What to create | Where |
|------|---------------|-------|
| 1 | Content record (`Project.cs`) | `Source/` |
| 2 | Reference data types (`Status.cs`, `Category.cs`, …) | `Source/` |
| 3 | CSV data loaders (optional) | `Source/DataLoader.cs` |
| 4 | Layout areas | `Source/ProjectLayoutAreas.cs` |
| 5 | NodeType JSON definition | `Project.json` in the parent folder |
| 6 | Unit tests | `Test/ProjectTests.cs` |
| 7 | CSV data files (optional) | `attachments/` folder |
