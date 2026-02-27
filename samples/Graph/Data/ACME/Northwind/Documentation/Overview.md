---
NodeType: "ACME/Northwind/Article"
Title: "Northwind Case Studies"
Abstract: "Learn MeshWeaver through the ACME Northwind gourmet food analytics sample"
Icon: "Document"
Published: "2025-01-31"
Authors:
  - "MeshWeaver Team"
Tags:
  - "Northwind"
  - "Getting Started"
---

The ACME Northwind sample demonstrates MeshWeaver capabilities through a realistic gourmet food distribution analytics scenario.

---

# What do you want to learn?

| Topic | Go here |
|-------|---------|
| Get up and running | [Getting Started](Northwind/Documentation/GettingStarted) - Setup, navigation, first steps |
| Understand the architecture | [Architecture](Northwind/Documentation/Architecture) - Data model, views, analytics pipeline |
| Add AI to your app | [AI Agent Integration](Northwind/Documentation/AIAgentIntegration) - NorthwindAgent, analytics queries |
| Reference paths and queries | [Unified References](Northwind/Documentation/UnifiedReferences) - Paths, queries, layout areas |

---

# The Northwind Organization

ACME Northwind is a gourmet food distribution company with analytics views for sales, customers, products, and employees:

```
Northwind/                           # Analytics platform
├── AnalyticsCatalog.json             # NodeType definition (53 views)
├── Analytics.json                   # Root database node
├── Data/                            # CSV data sources
│   ├── orders.csv                   # Order transactions
│   └── orders_details.csv           # Order line items
└── AnalyticsCatalog/Code/            # View implementations
    ├── DashboardViews.cs            # Main dashboard
    ├── SalesViews.cs                # Sales analytics
    ├── OrderViews.cs                # Order analysis
    ├── ProductViews.cs              # Product analytics
    ├── CustomerViews.cs             # Customer insights
    ├── EmployeeViews.cs             # Employee performance
    ├── SupplierViews.cs             # Supplier analysis
    ├── FinancialViews.cs            # Financial metrics
    └── InventoryViews.cs            # Inventory trends
```

---

# Key Concepts Demonstrated

## Virtual Data Cube

The `NorthwindDataCube` combines Orders, OrderDetails, and Products into a unified analytics layer:
- Enriched dimension names for charts
- Efficient aggregation across years
- Flexible filtering and grouping

## Multi-Year Analytics

Data covers 2024-2025 with year-over-year comparison:
- Year filtering via toolbar component
- Growth metrics and trend analysis
- Seasonal pattern identification

## Layout Area Organization

53 views organized into 8 categories:
- Dashboard, Sales, Orders, Products
- Customers, Employees, Suppliers, Financial

---

# Sample Data

## Data Volume

| Entity | Count | Description |
|--------|-------|-------------|
| Orders | 830 | Order transactions |
| OrderDetails | 2,155 | Order line items |
| Products | 77 | Gourmet food products |
| Categories | 8 | Product categories |
| Customers | 91 | Customers across 21 countries |
| Employees | 9 | Sales representatives and managers |
| Suppliers | 29 | Suppliers from 16 countries |

## Product Categories

| Category | Description |
|----------|-------------|
| Beverages | Soft drinks, coffees, teas, beers, ales |
| Condiments | Sweet and savory sauces, relishes, spreads |
| Confections | Desserts, candies, sweetbreads |
| Dairy Products | Cheeses |
| Grains/Cereals | Breads, crackers, pasta, cereal |
| Meat/Poultry | Prepared meats |
| Produce | Dried fruit and bean curd |
| Seafood | Seaweed and fish |

---

# Explore Further

Navigate to `Northwind` in the portal to explore:
- The main Dashboard with sales overview
- Sales analytics with category comparisons
- Customer insights and segmentation
- Product performance and trends
- The AI chat agent for natural language queries
