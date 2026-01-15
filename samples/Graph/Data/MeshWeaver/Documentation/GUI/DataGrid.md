---
Name: DataGrid Control
Category: Documentation
Description: Display collections of data with sortable, resizable columns
Icon: /static/storage/content/MeshWeaver/Documentation/GUI/DataGrid/icon.svg
---

The DataGrid control displays collections of data in a tabular format with sortable, resizable columns and optional virtualization for large datasets.

## Basic Usage

### Example 1: Simple DataGrid

```csharp
public record Product(string Name, decimal Price, int Stock);

var products = new[]
{
    new Product("Widget", 9.99m, 100),
    new Product("Gadget", 24.99m, 50),
    new Product("Gizmo", 14.99m, 75)
};

new DataGridControl(products)
    .WithColumn(new PropertyColumnControl<string>()
        .WithProperty("name")
        .WithTitle("Product Name"))
    .WithColumn(new PropertyColumnControl<decimal>()
        .WithProperty("price")
        .WithTitle("Price"))
    .WithColumn(new PropertyColumnControl<int>()
        .WithProperty("stock")
        .WithTitle("In Stock"))
```

**Result:** A table with 3 columns showing product data. Columns are sortable and resizable by default.

### Example 2: With Pagination

```csharp
new DataGridControl(largeDataset)
    .WithPagination(true)
    .WithItemsPerPage(10)
    .WithShowPageSizeSelector(true)
    .WithPageSizeOptions(new[] { 10, 25, 50, 100 })
```

**Result:** Data displayed in pages with navigation controls and a page size dropdown.

### Example 3: With Custom Template Column

```csharp
new DataGridControl(users)
    .WithColumn(new PropertyColumnControl<string>()
        .WithProperty("name")
        .WithTitle("Name"))
    .WithColumn(new TemplateColumnControl(
        Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap("4px")
            .WithView(Controls.Button("Edit"))
            .WithView(Controls.Button("Delete")))
        .WithTitle("Actions")
        .WithSortable(false))
```

**Result:** A Name column plus an Actions column with Edit/Delete buttons in each row.

### Example 4: Virtualized Large Dataset

```csharp
new DataGridControl(thousandsOfItems)
    .WithVirtualize(true)
    .WithItemSize(40)
    .WithGenerateHeader("Sticky")
```

**Result:** Only visible rows render to DOM. Header stays fixed while scrolling.

## Column Types

### PropertyColumnControl

Displays a property value from each row:

```csharp
new PropertyColumnControl<string>()
    .WithProperty("email")        // Property name (camelCase)
    .WithTitle("Email Address")   // Column header
    .WithSortable(true)           // Enable sorting (default)
    .WithResizable(true)          // Enable resizing (default)
    .WithWidth("200px")           // Fixed width
    .WithAlign("start")           // Cell alignment
```

### TemplateColumnControl

Custom content for each cell:

```csharp
new TemplateColumnControl(Controls.Button("View"))
    .WithTitle("Actions")
    .WithSortable(false)
    .WithWidth("100px")
```

## DataGrid Configuration

| Method | Purpose | Default |
|--------|---------|---------|
| `WithVirtualize(bool)` | Enable virtual scrolling | false |
| `WithItemSize(int)` | Row height in pixels | 50 |
| `Resizable(bool)` | Allow column resizing | true |
| `WithPagination(bool)` | Enable pagination | false |
| `WithItemsPerPage(int)` | Rows per page | - |
| `WithPageSizeOptions(int[])` | Page size choices | [5,10,25,50,100] |
| `WithShowHover(bool)` | Highlight row on hover | true |
| `WithSelectionMode(string)` | Row selection | - |
| `WithEmptyContent(control)` | Content when data is empty | - |
| `WithLoading(bool)` | Show loading state | false |

## Column Configuration

| Method | Purpose | Default |
|--------|---------|---------|
| `WithTitle(string)` | Column header text | - |
| `WithWidth(string)` | Fixed width | - |
| `WithMinWidth(string)` | Minimum width | - |
| `WithMaxWidth(string)` | Maximum width | - |
| `WithAlign(string)` | Cell alignment | - |
| `WithSortable(bool)` | Enable sorting | true |
| `WithResizable(bool)` | Enable resizing | true |
| `WithVisible(bool)` | Show/hide column | true |
| `WithFrozen(bool)` | Freeze column | false |

## Common Patterns

### Data Table with Actions

```csharp
public record User(string Name, string Email, DateTime Created);

new DataGridControl(users)
    .WithColumn(new PropertyColumnControl<string>()
        .WithProperty("name").WithTitle("Name"))
    .WithColumn(new PropertyColumnControl<string>()
        .WithProperty("email").WithTitle("Email"))
    .WithColumn(new PropertyColumnControl<DateTime>()
        .WithProperty("created").WithTitle("Created"))
    .WithColumn(new TemplateColumnControl(
        Controls.Button("View Details"))
        .WithTitle("").WithWidth("120px").WithSortable(false))
    .Resizable(true)
    .WithShowHover(true)
```

### Read-Only Report Grid

```csharp
new DataGridControl(reportData)
    .Resizable(false)
    .WithColumn(new PropertyColumnControl<string>()
        .WithProperty("category").WithResizable(false))
    .WithColumn(new PropertyColumnControl<decimal>()
        .WithProperty("amount").WithAlign("end"))
```

## See Also

- [Editor Control](MeshWeaver/Documentation/GUI/Editor) - Form generation
- [Stack Control](MeshWeaver/Documentation/GUI/Stack) - Layout container
