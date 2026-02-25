---
NodeType: "ACME/Northwind/Article"
Title: "Northwind Architecture"
Abstract: "Deep dive into Northwind's data loading, virtual data cube, and analytics view patterns"
Icon: "Document"
Published: "2025-01-31"
Authors:
  - "MeshWeaver Team"
Tags:
  - "Northwind"
  - "Architecture"
---

# Northwind Architecture

Understanding the architectural patterns in the Northwind sample helps you build scalable analytics applications with MeshWeaver. This document covers data loading, virtual data cubes, and view organization patterns.

## Node Structure

Northwind uses an AnalyticsCatalog NodeType to organize analytics views and data sources:

```
Northwind/                           # Root namespace
├── AnalyticsCatalog.json             # NodeType: 53 layout areas, data sources
├── Analytics.json                   # Instance: Root database node
├── Access/
│   └── Public.json                  # Public viewer access
├── Data/
│   ├── orders.csv                   # Order transactions
│   ├── orders_details.csv           # Order line items
│   ├── products.csv                 # Product catalog
│   ├── customers.csv                # Customer directory
│   ├── employees.csv                # Employee records
│   ├── suppliers.csv                # Supplier directory
│   ├── categories.csv               # Product categories
│   ├── regions.csv                  # Geographic regions
│   ├── territories.csv              # Sales territories
│   └── shippers.csv                 # Shipping companies
└── AnalyticsCatalog/Code/            # View implementations
    ├── Order.cs                     # Order entity
    ├── OrderDetails.cs              # OrderDetails entity
    ├── Product.cs                   # Product entity
    ├── Customer.cs                  # Customer entity
    ├── NorthwindDataCube.cs         # Virtual fact table
    ├── NorthwindDataLoader.cs       # CSV data loading
    ├── DashboardViews.cs            # Dashboard (1 view)
    ├── SalesViews.cs                # Sales analytics (6 views)
    ├── OrderViews.cs                # Order analysis (8 views)
    ├── ProductViews.cs              # Product analytics (9 views)
    ├── CustomerViews.cs             # Customer insights (11 views)
    ├── EmployeeViews.cs             # Employee performance (5 views)
    ├── SupplierViews.cs             # Supplier analysis (2 views)
    ├── FinancialViews.cs            # Financial metrics (8 views)
    └── InventoryViews.cs            # Inventory trends (3 views)
```

### NodeType vs Instance

| Node | Type | Purpose |
|------|------|---------|
| `Northwind/AnalyticsCatalog` | NodeType | Defines data sources, views, and behavior |
| `Northwind/Analytics` | Instance | Actual database with data |

## Data Loading Pattern

Northwind demonstrates CSV-based data loading with reactive updates.

### Data Source Configuration

The AnalyticsCatalog NodeType configures multiple data sources:

```csharp
config.AddData(data => data
    .AddSource(source => source
        // All entity data loaded from CSV files via NorthwindDataLoader
        .WithType<Order>(t => t.WithInitialData(NorthwindDataLoader.LoadOrdersAsync))
        .WithType<OrderDetails>(t => t.WithInitialData(NorthwindDataLoader.LoadOrderDetailsAsync))
        .WithType<Product>(t => t.WithInitialData(NorthwindDataLoader.LoadProductsAsync))
        .WithType<Customer>(t => t.WithInitialData(NorthwindDataLoader.LoadCustomersAsync))
        .WithType<Employee>(t => t.WithInitialData(NorthwindDataLoader.LoadEmployeesAsync))
        .WithType<Supplier>(t => t.WithInitialData(NorthwindDataLoader.LoadSuppliersAsync))
        .WithType<Category>(t => t.WithInitialData(NorthwindDataLoader.LoadCategoriesAsync))
        .WithType<Region>(t => t.WithInitialData(NorthwindDataLoader.LoadRegionsAsync))
        .WithType<Territory>(t => t.WithInitialData(NorthwindDataLoader.LoadTerritoriesAsync))
        .WithType<Shipper>(t => t.WithInitialData(NorthwindDataLoader.LoadShippersAsync)))
    // Virtual data cube
    .WithVirtualDataSource("NorthwindDataCube", ...))
```

### CSV Loading (`NorthwindDataLoader.cs`)

```csharp
public static IEnumerable<Order> LoadOrders(string csvPath)
{
    return File.ReadLines(csvPath)
        .Skip(1) // Header
        .Select(line => line.Split(','))
        .Select(parts => new Order
        {
            OrderId = int.Parse(parts[0]),
            CustomerId = parts[1],
            EmployeeId = int.Parse(parts[2]),
            OrderDate = DateTime.Parse(parts[3]),
            Freight = decimal.Parse(parts[7]),
            ShipCountry = parts[13]
        });
}
```

### Reference Data (CSV-based)

All reference data is loaded from CSV files via `NorthwindDataLoader.cs`, following the same pattern as transactional data:

```csharp
public static Task<IEnumerable<Category>> LoadCategoriesAsync(CancellationToken ct)
{
    // CSV: categoryid,categoryname,description,picture
    var lines = File.ReadAllLines(Path.Combine(BasePath, "categories.csv"));
    return Task.FromResult(ParseCsv(lines, parts => new Category
    {
        CategoryId = ParseInt(parts[0]),
        CategoryName = parts[1],
        Description = Get(parts, 2),
    }));
}
```

## Virtual Data Cube

The `NorthwindDataCube` is the key analytics pattern - a denormalized fact table combining Orders, OrderDetails, and Products.

### Cube Definition (`NorthwindDataCube.cs`)

```csharp
public record NorthwindDataCube
{
    // Keys
    public int OrderId { get; init; }
    public int ProductId { get; init; }

    // Dimensions
    public string ProductName { get; init; }
    public string CategoryName { get; init; }
    public string CustomerName { get; init; }
    public string EmployeeName { get; init; }
    public string ShipCountry { get; init; }
    public int Year { get; init; }
    public int Month { get; init; }

    // Measures
    public decimal UnitPrice { get; init; }
    public int Quantity { get; init; }
    public decimal Discount { get; init; }
    public decimal Revenue { get; init; }
    public decimal DiscountAmount { get; init; }
}
```

### Cube Population

The cube is populated reactively from source data:

```csharp
public static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(
    IObservable<IEnumerable<Order>> orders,
    IObservable<IEnumerable<OrderDetails>> details,
    IObservable<IEnumerable<Product>> products)
{
    return orders.CombineLatest(details, products, (o, d, p) =>
    {
        var productLookup = p.ToDictionary(x => x.ProductId);
        var orderLookup = o.ToDictionary(x => x.OrderId);

        return d.Select(detail =>
        {
            var order = orderLookup[detail.OrderId];
            var product = productLookup[detail.ProductId];
            var revenue = detail.UnitPrice * detail.Quantity * (1 - detail.Discount);

            return new NorthwindDataCube
            {
                OrderId = detail.OrderId,
                ProductId = detail.ProductId,
                ProductName = product.ProductName,
                CategoryName = product.CategoryName,
                CustomerName = order.CustomerName,
                Year = order.OrderDate.Year,
                Month = order.OrderDate.Month,
                Revenue = revenue,
                // ...
            };
        });
    });
}
```

### Benefits of Virtual Data Cubes

1. **Single Source of Truth**: All views query the same cube
2. **Consistent Calculations**: Revenue, discount calculations centralized
3. **Efficient Queries**: Pre-joined data reduces query complexity
4. **Reactive Updates**: Changes propagate automatically

## View Organization

Views are organized into 8 categories with 53 total views.

### View Groups

| Group | Order | Views | Purpose |
|-------|-------|-------|---------|
| Dashboards | - | 1 | Main overview |
| Customers | 120 | 11 | Customer analytics |
| Sales | 200 | 6 | Sales by category/region |
| Employees | 300 | 5 | Employee performance |
| Products | 400 | 9 | Product analytics |
| Suppliers | 420 | 2 | Supplier metrics |
| Orders | 600 | 8 | Order analysis |
| Financial | 700 | 8 | Financial metrics |
| Inventory | 800 | 3 | Inventory trends |

### View Registration

Views are registered in the AnalyticsCatalog NodeType:

```csharp
config.AddLayout(layout => layout
    .AddDashboardViews()
    .AddSalesViews()
    .AddOrderViews()
    .AddProductViews()
    .AddCustomerViews()
    .AddEmployeeViews()
    .AddSupplierViews()
    .AddFinancialViews()
    .AddInventoryViews())
```

### View Implementation Pattern

Each view follows a reactive pattern using the data cube:

```csharp
public static IObservable<UiControl?> SalesByCategory(LayoutAreaHost host, RenderingContext ctx)
{
    return host.GetDataCube<NorthwindDataCube>()
        .CombineLatest(host.GetYearFilter(), (cube, year) =>
        {
            var filtered = year.HasValue
                ? cube.Where(x => x.Year == year.Value)
                : cube;

            var data = filtered
                .GroupBy(x => x.CategoryName)
                .Select(g => new { Category = g.Key, Revenue = g.Sum(x => x.Revenue) })
                .OrderByDescending(x => x.Revenue);

            return (UiControl?)new BarChart
            {
                Title = "Sales by Category",
                Data = data,
                XAxis = "Category",
                YAxis = "Revenue"
            };
        });
}
```

## Year Filtering Architecture

Northwind supports multi-year analysis through a toolbar component.

### Toolbar Component (`NorthwindYearToolbar.cs`)

```csharp
public static UiControl YearToolbar(LayoutAreaHost host)
{
    var years = new[] { "All Years", "2025", "2024" };

    return new ToolbarControl()
        .WithDropdown("Year", years, selectedYear =>
        {
            host.SetContextValue("SelectedYear",
                selectedYear == "All Years" ? null : int.Parse(selectedYear));
        });
}
```

### Year Filter Observable

Views subscribe to the year filter:

```csharp
public static IObservable<int?> GetYearFilter(this LayoutAreaHost host)
{
    return host.GetContextValue<int?>("SelectedYear")
        .StartWith((int?)null);
}
```

### Filter Application

Views apply the filter to their data cube queries:

```csharp
var filteredData = cube
    .CombineLatest(yearFilter, (data, year) =>
        year.HasValue
            ? data.Where(x => x.Year == year.Value)
            : data);
```

## View Types

Northwind uses several view patterns for different analytics needs.

### Chart Views

```csharp
// Bar chart for category comparison
public static UiControl SalesByCategory(data) => new BarChart
{
    Data = data,
    XAxis = "Category",
    YAxis = "Revenue",
    Title = "Sales by Category"
};

// Line chart for trends
public static UiControl RevenueTrend(data) => new LineChart
{
    Data = data,
    XAxis = "Month",
    YAxis = "Revenue",
    Title = "Monthly Revenue Trend"
};

// Pie chart for distribution
public static UiControl CategoryDistribution(data) => new PieChart
{
    Data = data,
    Label = "Category",
    Value = "Revenue"
};
```

### Data Grid Views

```csharp
public static UiControl ProductOverview(data) => new DataGrid
{
    Data = data,
    Columns = new[]
    {
        new Column("Product", x => x.ProductName),
        new Column("Category", x => x.CategoryName),
        new Column("Revenue", x => x.Revenue, Format.Currency),
        new Column("Quantity", x => x.Quantity),
        new Column("Discount", x => x.DiscountAmount, Format.Currency)
    }
};
```

### Markdown Report Views

```csharp
public static UiControl OrdersSummaryReport(data) => new MarkdownControl
{
    Content = $"""
    ## Orders Summary Report

    **Total Orders**: {data.Count()}
    **Total Revenue**: {data.Sum(x => x.Revenue):C}
    **Average Order Value**: {data.Average(x => x.Revenue):C}

    ### Monthly Breakdown

    | Month | Orders | Revenue |
    |-------|--------|---------|
    {FormatMonthlyTable(data)}
    """
};
```

## Cross-References

For related MeshWeaver architecture concepts:

- **[Mesh Graph Architecture](MeshWeaver/Documentation/Architecture/MeshGraph)**: Node structure and namespaces
- **[Layout System](MeshWeaver/Documentation/GUI)**: View registration and rendering
- **[Data Mesh](MeshWeaver/Documentation/DataMesh)**: Data source patterns
- **[AI Agent Integration](Northwind/Documentation/AIAgentIntegration)**: NorthwindAgent usage

## Conclusion

The Northwind architecture demonstrates key patterns for analytics applications:

1. **Virtual Data Cubes**: Denormalized fact tables for efficient queries
2. **Reactive Views**: Observables for automatic UI updates
3. **Year Filtering**: Context-based filtering via toolbar
4. **View Organization**: Grouped views for logical navigation
5. **Multiple View Types**: Charts, grids, and reports for different needs

These patterns scale from small samples to enterprise analytics applications.
