---
Name: Displaying Data in a UI
Category: Documentation
Description: Display collections of data with sortable, resizable columns and optional virtualization for large datasets
Icon: /static/DocContent/GUI/DataGrid/icon.svg
---

`DataGridControl` renders any collection as a tabular layout with sortable, resizable columns. It supports pagination, virtual scrolling for large datasets, custom cell templates, and column-level formatting — all wired up with a fluent builder API.

---

# Basic Usage

The minimum setup is a `DataGridControl(data)` call plus one or more column definitions.

```csharp --render DataGridSimple --show-code
record Product(string Name, decimal Price, int Stock);

var products = new[]
{
    new Product("Widget", 9.99m, 100),
    new Product("Gadget", 24.99m, 50),
    new Product("Gizmo", 14.99m, 75)
};

new DataGridControl(products)
    .WithColumn(new PropertyColumnControl<string>  { Property = "name"  }.WithTitle("Product Name"))
    .WithColumn(new PropertyColumnControl<decimal> { Property = "price" }.WithTitle("Price"))
    .WithColumn(new PropertyColumnControl<int>     { Property = "stock" }.WithTitle("In Stock"))
```

> **Property names are camelCase.** The `Property` value on `PropertyColumnControl` must match the camelCase form of the record/class property name (e.g. `"name"` for `Name`, `"unitPrice"` for `UnitPrice`).

---

# Column Types

## PropertyColumnControl

Renders the value of a typed property from each row. The generic type parameter controls sorting and formatting behaviour.

```csharp
new PropertyColumnControl<string> { Property = "email" }
    .WithTitle("Email Address")
    .WithWidth("200px")
    .WithSortable(true)       // default: true
    .WithResizable(true)      // default: true
    .WithAlign("end")         // start | center | end
    .WithDefaultSort()        // make this the initial sort column
    .WithFormat("C2")         // standard .NET format string (numbers, dates)
```

## TemplateColumnControl

Places an arbitrary `UiControl` in every cell of the column. Use this for action buttons, badges, or any custom rendering.

```csharp
new TemplateColumnControl(Controls.Button("View"))
    .WithTitle("Actions")
    .WithSortable(false)   // action columns are rarely sortable
    .WithWidth("100px")
```

---

# Configuration Reference

## DataGrid options

| Method | Purpose | Default |
|---|---|---|
| `WithVirtualize(bool)` | Enable virtual (windowed) scrolling | `false` |
| `WithItemSize(int)` | Row height in pixels (used by virtualizer) | `50` |
| `Resizable(bool)` | Allow column resizing globally | `true` |
| `WithPagination(bool)` | Enable built-in pagination | `false` |
| `WithItemsPerPage(int)` | Rows per page | — |
| `WithPageSizeOptions(int[])` | Available page size choices | `[5,10,25,50,100]` |
| `WithShowHover(bool)` | Highlight row under the pointer | `true` |
| `WithSelectionMode(string)` | Row selection mode | — |
| `WithEmptyContent(control)` | Content shown when the dataset is empty | — |
| `WithLoading(bool)` | Show loading skeleton | `false` |
| `WithGenerateHeader(string)` | Header generation strategy (`"Sticky"`, etc.) | `"Sticky"` |

## Column options (all column types)

| Method | Purpose | Default |
|---|---|---|
| `WithTitle(string)` | Column header text | — |
| `WithWidth(string)` | Fixed CSS width | — |
| `WithMinWidth(string)` | Minimum CSS width | — |
| `WithMaxWidth(string)` | Maximum CSS width | — |
| `WithAlign(string)` | Cell alignment (`start`, `center`, `end`) | — |
| `WithSortable(bool)` | Enable column sorting | `true` |
| `WithResizable(bool)` | Enable column resizing | `true` |
| `WithVisible(bool)` | Show or hide the column | `true` |
| `WithFrozen(bool)` | Freeze column (pin to left edge) | `false` |
| `WithFilterable(bool)` | Enable column filtering | — |

## PropertyColumnControl extras

| Method | Purpose |
|---|---|
| `WithFormat(string)` | .NET format string (`"C2"`, `"d"`, etc.) |
| `WithDefaultSort()` | Make this the initial sort column |
| `WithInitialSortDirection(dir)` | `"Ascending"` or `"Descending"` |
| `WithEditable()` | Allow inline editing |
| `WithPlaceholderText(string)` | Placeholder shown for null/empty cells |
| `WithNullDisplayText(string)` | Text rendered when value is `null` |

---

# Common Patterns

## Pagination for longer lists

```csharp --render DataGridPagination --show-code
record Item(int Id, string Name, string Category);

var items = Enumerable.Range(1, 25)
    .Select(i => new Item(i, $"Item {i}", i % 2 == 0 ? "A" : "B"))
    .ToArray();

new DataGridControl(items)
    .WithPagination(true)
    .WithItemsPerPage(5)
    .WithColumn(new PropertyColumnControl<int>    { Property = "id"       }.WithTitle("ID"))
    .WithColumn(new PropertyColumnControl<string> { Property = "name"     }.WithTitle("Name"))
    .WithColumn(new PropertyColumnControl<string> { Property = "category" }.WithTitle("Category"))
```

## Action buttons

Use `TemplateColumnControl` for per-row commands. Disable sorting on it so users aren't confused by clicking the header.

```csharp --render DataGridActions --show-code
record User(string Name, string Email);

var users = new[]
{
    new User("Alice", "alice@example.com"),
    new User("Bob",   "bob@example.com"),
    new User("Carol", "carol@example.com")
};

var actionButtons = Controls.Stack
    .WithOrientation(Orientation.Horizontal)
    .WithHorizontalGap("4px")
    .WithView(Controls.Button("Edit"))
    .WithView(Controls.Button("Delete"));

new DataGridControl(users)
    .WithColumn(new PropertyColumnControl<string> { Property = "name"  }.WithTitle("Name"))
    .WithColumn(new PropertyColumnControl<string> { Property = "email" }.WithTitle("Email"))
    .WithColumn(new TemplateColumnControl(actionButtons)
        .WithTitle("Actions").WithSortable(false))
```

## Virtual scrolling for large datasets

Enable `WithVirtualize(true)` together with a fixed `WithItemSize` so the renderer can calculate offsets without measuring every row.

```csharp --render DataGridVirtualized --show-code
record DataRow(int Id, string Value);

var largeDataset = Enumerable.Range(1, 100)
    .Select(i => new DataRow(i, $"Row {i}"))
    .ToArray();

new DataGridControl(largeDataset)
    .WithVirtualize(true)
    .WithItemSize(40)
    .WithColumn(new PropertyColumnControl<int>    { Property = "id"    }.WithTitle("ID"))
    .WithColumn(new PropertyColumnControl<string> { Property = "value" }.WithTitle("Value"))
```

## Read-only report grid

For display-only tables, disable resizing and right-align numeric columns.

```csharp --render PatternReport --show-code
record Report(string Category, decimal Amount);

var reportData = new[]
{
    new Report("Sales",      15000.00m),
    new Report("Marketing",   8500.50m),
    new Report("Operations", 12300.75m)
};

new DataGridControl(reportData)
    .Resizable(false)
    .WithColumn(new PropertyColumnControl<string>  { Property = "category" }
        .WithTitle("Category").WithResizable(false))
    .WithColumn(new PropertyColumnControl<decimal> { Property = "amount" }
        .WithTitle("Amount").WithAlign("end").WithFormat("C2"))
```

## Table with action column

A complete employee directory showing name, email, department, and a "View Details" action — a common production pattern.

```csharp --render PatternActions --show-code
record Employee(string Name, string Email, string Dept);

var employees = new[]
{
    new Employee("Alice", "alice@co.com", "Engineering"),
    new Employee("Bob",   "bob@co.com",   "Marketing"),
    new Employee("Carol", "carol@co.com", "Sales")
};

new DataGridControl(employees)
    .WithColumn(new PropertyColumnControl<string> { Property = "name"  }.WithTitle("Name"))
    .WithColumn(new PropertyColumnControl<string> { Property = "email" }.WithTitle("Email"))
    .WithColumn(new PropertyColumnControl<string> { Property = "dept"  }.WithTitle("Department"))
    .WithColumn(new TemplateColumnControl(Controls.Button("View Details"))
        .WithTitle("").WithWidth("120px").WithSortable(false))
    .Resizable(true)
    .WithShowHover(true)
```

---

# See Also

- [Editor Control](../Editor) — Form generation
- [Stack Control](../ContainerControl/Stack) — Layout container
