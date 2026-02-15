---
Name: Getting Started with Northwind
Category: Case Studies
Description: Explore the Northwind Traders analytics sample and learn MeshWeaver data visualization through practical examples
Icon: /static/storage/content/MeshWeaver/Documentation/Northwind/icon.svg
---

# Getting Started with Northwind

The Northwind Traders sample demonstrates MeshWeaver's analytics capabilities through a realistic business scenario: a gourmet food distribution company with sales, customer, product, and employee analytics.

## The Northwind Sample

Northwind demonstrates how MeshWeaver handles analytics workloads:

### Data Overview

| Entity | Count | Description |
|--------|-------|-------------|
| Orders | 830 | Order transactions (2024-2025) |
| OrderDetails | 2,155 | Line items with pricing and discounts |
| Products | 77 | Products across 8 categories |
| Customers | 91 | Customers in 21 countries |
| Employees | 9 | Sales representatives and managers |
| Suppliers | 29 | Suppliers from 16 countries |

### Key Concepts Demonstrated

1. **CSV Data Loading**: Orders and order details loaded from CSV files
2. **Virtual Data Cube**: `NorthwindDataCube` combines data for efficient analytics
3. **View Organization**: 53 views across 8 categories (Dashboard, Sales, Orders, etc.)
4. **Year Filtering**: Toolbar component for multi-year analysis

### Product Categories

Northwind specializes in gourmet foods across 8 categories:

| Category | Products | Description |
|----------|----------|-------------|
| Beverages | 12 | Soft drinks, coffees, teas, beers, ales |
| Condiments | 12 | Sweet and savory sauces, relishes, spreads |
| Confections | 13 | Desserts, candies, sweetbreads |
| Dairy Products | 10 | Cheeses |
| Grains/Cereals | 7 | Breads, crackers, pasta, cereal |
| Meat/Poultry | 6 | Prepared meats |
| Produce | 5 | Dried fruit and bean curd |
| Seafood | 12 | Seaweed and fish |

## Running the Sample

### Clone and Build

```bash
git clone https://github.com/MeshWeaver/MeshWeaver.git
cd MeshWeaver
dotnet build
```

### Start the Portal

```bash
cd memex/Memex.Portal.Monolith
dotnet run
```

Navigate to `http://localhost:7122` in your browser.

### Navigate to Northwind

1. Navigate to **Northwind** organization
2. Select **Analytics** catalog
3. Explore views using the category tabs

## Exploring the Dashboard

### Main Dashboard

@@Northwind/Dashboard

The main dashboard provides a 4-panel overview:
- **Top Orders**: Highest value orders by customer
- **Sales by Category**: Revenue breakdown by product category
- **Supplier Summary**: Key supplier metrics
- **Top Products**: Best-selling products by revenue

### Year Filtering

Use the toolbar dropdown to filter analytics by year:
- **All Years**: Complete data from 2024-2025
- **2025**: Current year data only
- **2024**: Previous year comparison

## Working with Sales Analytics

### Sales by Category

@@Northwind/SalesByCategory

Shows total revenue for each product category. Key insights:
- Beverages and Dairy Products lead with ~40% of total revenue
- Seafood and Condiments are strong performers
- Growth opportunities in Grains/Cereals and Produce

### Year-over-Year Comparison

@@Northwind/SalesByCategoryComparison

Compare category performance across years to identify:
- Growth trends by category
- Seasonal patterns
- Market share shifts

## Customer Insights

### Top Customers

@@Northwind/TopClients

Identifies top 5 customers by revenue with metrics:
- Total order value
- Order count
- Average order size

### Customer Segmentation

@@Northwind/CustomerSegmentation

Segments customers into tiers based on purchase behavior:
- VIP, High Value, Regular, Occasional, New

## Product Analytics

### Product Overview

@@Northwind/ProductOverview

Data grid showing all products with:
- Revenue and quantity sold
- Discount amounts applied
- Category classification

### Top Products

@@Northwind/TopProducts

Charts showing best-performing products by:
- Total revenue
- Units sold
- Revenue per unit

## Using the Northwind Agent

The AI analytics agent helps query and visualize data using natural language.

### Querying Sales Data

```
"Show me sales by category for 2025"
→ Displays SalesByCategory view for selected year

"Compare revenue between 2024 and 2025"
→ Shows SalesByCategoryComparison with both years
```

### Customer Analysis

```
"Who are our top customers?"
→ Displays TopClients view with customer rankings

"Show customer geographic distribution"
→ Displays CustomerGeographicDistribution map
```

### Product Queries

```
"What are our best-selling products?"
→ Displays TopProducts chart

"Show product performance trends"
→ Displays ProductPerformanceTrends line chart
```

### Context-Aware Operations

The agent understands:
- **Product Categories**: Maps queries to specific categories
- **Time Periods**: Handles year filtering and comparisons
- **Metrics**: Revenue, orders, quantities, discounts
- **Entities**: Customers, products, employees, suppliers

## Understanding the Code

### Data Model (`Order.cs`)

```csharp
public record Order
{
    public int OrderId { get; init; }
    public string CustomerId { get; init; }
    public int EmployeeId { get; init; }
    public DateTime OrderDate { get; init; }
    public decimal Freight { get; init; }
    public string ShipCountry { get; init; }
}
```

### Virtual Data Cube (`NorthwindDataCube.cs`)

```csharp
public record NorthwindDataCube
{
    public int OrderId { get; init; }
    public int ProductId { get; init; }
    public string ProductName { get; init; }
    public string CategoryName { get; init; }
    public decimal UnitPrice { get; init; }
    public int Quantity { get; init; }
    public decimal Discount { get; init; }
    public decimal Revenue { get; init; }
}
```

### View Example (`SalesViews.cs`)

```csharp
public static IObservable<UiControl?> SalesByCategory(LayoutAreaHost host, RenderingContext _)
{
    return host.GetDataCube<NorthwindDataCube>()
        .Select(cube => cube
            .GroupBy(x => x.CategoryName)
            .Select(g => new { Category = g.Key, Revenue = g.Sum(x => x.Revenue) })
            .OrderByDescending(x => x.Revenue))
        .Select(data => new BarChart { Data = data });
}
```

## Architecture Deep Dive

For detailed understanding of the Northwind architecture:

- **[Northwind Architecture](MeshWeaver/Documentation/Northwind/Architecture)**: Data loading, virtual cube, view patterns
- **[AI Agent Integration](MeshWeaver/Documentation/Northwind/AIAgentIntegration)**: NorthwindAgent capabilities
- **[Unified References](MeshWeaver/Documentation/Northwind/UnifiedReferences)**: All addressable paths

## Next Steps

1. **Explore the Dashboard**: Navigate views and filter by year
2. **Use the Agent**: Try natural language queries in the chat interface
3. **Compare Years**: Use year-over-year views to identify trends
4. **Examine Customer Data**: Explore segmentation and geographic distribution
5. **Review Product Performance**: Identify top performers and growth opportunities

The Northwind sample provides a complete working example of MeshWeaver's analytics capabilities, from data loading and virtual cubes to AI-assisted data exploration.
