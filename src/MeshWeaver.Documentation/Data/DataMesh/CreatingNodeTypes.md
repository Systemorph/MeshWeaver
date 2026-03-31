---
Name: Creating Node Types
Category: Documentation
Description: Step-by-step guide to creating custom node types with _Source code, data models, layout areas, and CSV data loading
Icon: Code
---

# Creating Node Types

This guide walks through creating a custom NodeType with compiled source code, layout areas, reference data, and CSV data loading.

## Folder Structure

A NodeType lives in a folder under a namespace. Source code goes in `_Source/` and tests in `_Test/`:

```
samples/Graph/Data/
  ACME/
    Project.json              # NodeType definition (nodeType: "NodeType")
    Project/
      _Source/                # C# code compiled at startup
        Project.cs            # Content type (record)
        Status.cs             # Reference data type
        Category.cs           # Reference data type
        Priority.cs           # Reference data type
        ProjectLayoutAreas.cs # Custom views
      _Test/                  # xUnit tests for the source code
        ProjectTests.cs
      Todo.json               # Child NodeType definition
      Todo/
        _Source/
          Todo.cs             # Child content type
          TodoLayoutAreas.cs  # Child views
```

## Step 1: Define the Content Type

Create a C# record in `_Source/` with the `<meshweaver>` frontmatter:

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

### Key Attributes

| Attribute | Purpose |
|-----------|---------|
| `[Key]` | Primary identifier field |
| `[Required]` | Validation: field must be set |
| `[MeshNodeProperty(nameof(MeshNode.Name))]` | Maps to the MeshNode's Name property |
| `[MeshNodeProperty(nameof(MeshNode.Icon))]` | Maps to the MeshNode's Icon property |
| `[Dimension<Category>]` | References a lookup/dimension type |
| `[Markdown(EditorHeight = "200px")]` | Rich text editor for this field |
| `[UiControl(Style = "width: 200px;")]` | Controls form layout |
| `[Browsable(false)]` | Hides from UI |
| `[DisplayName("Due Date")]` | Custom label in forms |

### Content Type with Dimensions

For a child type that references lookup data:

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

## Step 2: Define Reference Data Types

Reference data types provide lookup values for `[Dimension<T>]` fields:

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
        Id = "Pending", Name = "Pending", Emoji = "\u23f3", Order = 0
    };

    public static readonly Status InProgress = new()
    {
        Id = "InProgress", Name = "In Progress", Emoji = "\ud83d\udd04", Order = 1
    };

    public static readonly Status Completed = new()
    {
        Id = "Completed", Name = "Completed", Emoji = "\u2705", Order = 4,
        IsExpandedByDefault = false
    };

    public static readonly Status[] All = [Pending, InProgress, Completed];

    public static Status GetById(string? id) =>
        All.FirstOrDefault(s => s.Id == id) ?? Pending;
}
```

## Step 3: Create the NodeType Definition (JSON)

The NodeType definition is a JSON file at the parent level. The `configuration` field contains a C# lambda expression that wires up the content type, data sources, and layout:

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

### Configuration Explained

- **`WithContentType<T>()`** registers the content record type for the editor form
- **`AddData()`** configures the `MeshDataSource` with reference data and virtual types
- **`AddSource(source => source.WithType<T>(...))`** registers types in the data source
- **`WithInitialData(T[] items)`** seeds reference data from static arrays
- **`WithInitialData(Func<CancellationToken, Task<IEnumerable<T>>>)`** seeds data from async loaders (e.g., CSV)
- **`AddLayout()`** configures views and layout areas

## Step 4: Loading Data from CSV Files

For types that load data from CSV files (like Northwind), define a loader in `_Source/`:

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

### Key Points for CSV Data

- Place CSV files in an `attachments/` folder and reference via `AddContentCollection`
- The loader reads CSV, skips the header row, and maps columns to record properties
- Use `WithInitialData(Func<CancellationToken, Task<IEnumerable<T>>>)` for async CSV loading
- Use `WithInitialData(T[])` for static in-memory reference data
- `[Dimension(typeof(T))]` creates relationships between types for join operations
- Implement `INamed` to provide a display name for lookup columns

## Step 5: Create Layout Areas

Define custom views in `_Source/` as static classes:

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
                    // Build view from data
                    return Controls.Stack
                        .WithView(Controls.Html("<h2>Dashboard</h2>"));
                });
    }
}
```

## Child NodeType Definitions

Child types are defined in subfolders. Their configuration references the parent's data via `AddHubSource`:

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

The `AddHubSource(parentAddress, ...)` imports types from the parent node's data source, so child instances can use the same reference data.

## Summary

| Step | What | Where |
|------|------|-------|
| 1 | Content type (record) | `_Source/MyType.cs` |
| 2 | Reference data types | `_Source/Status.cs`, etc. |
| 3 | CSV data loaders | `_Source/DataLoader.cs` |
| 4 | Layout areas | `_Source/MyTypeLayoutAreas.cs` |
| 5 | NodeType JSON | `MyType.json` in parent folder |
| 6 | Tests | `_Test/MyTypeTests.cs` |
| 7 | CSV files | `attachments/` folder |
