---
Name: Displaying Data in a UI
Category: Documentation
Description: Display collections of data with sortable, resizable columns
Icon: /static/storage/content/Doc/GUI/DataGrid/icon.svg
---

The DataGrid control displays collections of data in a tabular format with sortable, resizable columns and optional virtualization for large datasets.

# Basic Usage

## Simple DataGrid

```csharp --render DataGridSimple --show-code
record Product(string Name, decimal Price, int Stock);  // Define data record

var products = new[]                                    // Create sample data
{
    new Product("Widget", 9.99m, 100),                  // First product
    new Product("Gadget", 24.99m, 50),                  // Second product
    new Product("Gizmo", 14.99m, 75)                    // Third product
};

new DataGridControl(products)                           // Create grid with data
    .WithColumn(new PropertyColumnControl<string>       // Add Name column
        { Property = "name" }                           // Map to Name property
        .WithTitle("Product Name"))                     // Set column header
    .WithColumn(new PropertyColumnControl<decimal>      // Add Price column
        { Property = "price" }                          // Map to Price property
        .WithTitle("Price"))                            // Set column header
    .WithColumn(new PropertyColumnControl<int>          // Add Stock column
        { Property = "stock" }                          // Map to Stock property
        .WithTitle("In Stock"))                         // Set column header
```

---

## With Pagination

```csharp --render DataGridPagination --show-code
record Item(int Id, string Name, string Category);      // Define data record

var items = Enumerable.Range(1, 25)                     // Create 25 items
    .Select(i => new Item(i, $"Item {i}", i % 2 == 0 ? "A" : "B"))
    .ToArray();

new DataGridControl(items)                              // Create grid with data
    .WithPagination(true)                               // Enable pagination
    .WithItemsPerPage(5)                                // Show 5 rows per page
    .WithColumn(new PropertyColumnControl<int>          // Add Id column
        { Property = "id" }.WithTitle("ID"))
    .WithColumn(new PropertyColumnControl<string>       // Add Name column
        { Property = "name" }.WithTitle("Name"))
    .WithColumn(new PropertyColumnControl<string>       // Add Category column
        { Property = "category" }.WithTitle("Category"))
```

---

## With Action Buttons

```csharp --render DataGridActions --show-code
record User(string Name, string Email);                 // Define data record

var users = new[]                                       // Create sample data
{
    new User("Alice", "alice@example.com"),             // First user
    new User("Bob", "bob@example.com"),                 // Second user
    new User("Carol", "carol@example.com")              // Third user
};

var actionButtons = Controls.Stack                      // Create action button template
    .WithOrientation(Orientation.Horizontal)            // Horizontal layout
    .WithHorizontalGap("4px")                           // Gap between buttons
    .WithView(Controls.Button("Edit"))                  // Edit button
    .WithView(Controls.Button("Delete"));               // Delete button

new DataGridControl(users)                              // Create grid with data
    .WithColumn(new PropertyColumnControl<string>       // Add Name column
        { Property = "name" }.WithTitle("Name"))
    .WithColumn(new PropertyColumnControl<string>       // Add Email column
        { Property = "email" }.WithTitle("Email"))
    .WithColumn(new TemplateColumnControl(actionButtons)// Add Actions column
        .WithTitle("Actions").WithSortable(false))      // Disable sorting
```

---

## Virtualized Large Dataset

```csharp --render DataGridVirtualized --show-code
record DataRow(int Id, string Value);                   // Define data record

var largeDataset = Enumerable.Range(1, 100)             // Create 100 rows
    .Select(i => new DataRow(i, $"Row {i}"))
    .ToArray();

new DataGridControl(largeDataset)                       // Create grid with data
    .WithVirtualize(true)                               // Enable virtual scrolling
    .WithItemSize(40)                                   // Row height in pixels
    .WithColumn(new PropertyColumnControl<int>          // Add Id column
        { Property = "id" }.WithTitle("ID"))
    .WithColumn(new PropertyColumnControl<string>       // Add Value column
        { Property = "value" }.WithTitle("Value"))
```

---

# Column Types

## PropertyColumnControl

Displays a property value from each row. Configure columns with these methods:

```csharp
new PropertyColumnControl<string>                       // Column for string property
    { Property = "email" }                              // Property name (camelCase)
    .WithTitle("Email Address")                         // Column header
    .WithWidth("200px")                                 // Fixed width
    .WithSortable(true)                                 // Enable sorting (default: true)
    .WithResizable(true)                                // Enable resizing (default: true)
    .WithAlign("start")                                 // Cell alignment: start, center, end
```

## TemplateColumnControl

Custom content for each cell - useful for action buttons:

```csharp
new TemplateColumnControl(Controls.Button("View"))      // Control to render in each row
    .WithTitle("Actions")                               // Column header
    .WithSortable(false)                                // Disable sorting for action columns
    .WithWidth("100px")                                 // Fixed width
```

# DataGrid Configuration

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

# Column Configuration

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

# Common Patterns

## Data Table with Actions

```csharp --render PatternActions --show-code
record Employee(string Name, string Email, string Dept); // Define data record

var employees = new[]                                   // Create sample data
{
    new Employee("Alice", "alice@co.com", "Engineering"),
    new Employee("Bob", "bob@co.com", "Marketing"),
    new Employee("Carol", "carol@co.com", "Sales")
};

new DataGridControl(employees)                          // Create grid with data
    .WithColumn(new PropertyColumnControl<string>       // Add Name column
        { Property = "name" }.WithTitle("Name"))
    .WithColumn(new PropertyColumnControl<string>       // Add Email column
        { Property = "email" }.WithTitle("Email"))
    .WithColumn(new PropertyColumnControl<string>       // Add Department column
        { Property = "dept" }.WithTitle("Department"))
    .WithColumn(new TemplateColumnControl(              // Add Actions column
        Controls.Button("View Details"))                // Action button
        .WithTitle("").WithWidth("120px").WithSortable(false))
    .Resizable(true)                                    // Allow column resizing
    .WithShowHover(true)                                // Highlight on hover
```

---

## Read-Only Report Grid

```csharp --render PatternReport --show-code
record Report(string Category, decimal Amount);         // Define data record

var reportData = new[]                                  // Create sample data
{
    new Report("Sales", 15000.00m),
    new Report("Marketing", 8500.50m),
    new Report("Operations", 12300.75m)
};

new DataGridControl(reportData)                         // Create grid with data
    .Resizable(false)                                   // Disable column resizing
    .WithColumn(new PropertyColumnControl<string>       // Add Category column
        { Property = "category" }                       // Map to Category property
        .WithTitle("Category")                          // Column header
        .WithResizable(false))                          // No resizing
    .WithColumn(new PropertyColumnControl<decimal>      // Add Amount column
        { Property = "amount" }                         // Map to Amount property
        .WithTitle("Amount")                            // Column header
        .WithAlign("end"))                              // Right-align numbers
```

---

# See Also

- [Editor Control](Doc/GUI/Editor) - Form generation
- [Stack Control](Doc/GUI/ContainerControl/Stack) - Layout container
