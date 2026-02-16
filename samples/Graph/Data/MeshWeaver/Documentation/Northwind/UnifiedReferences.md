---
Name: Unified References in Northwind
Category: Case Studies
Description: Reference guide for namespace paths, data queries, and layout areas in the Northwind sample
Icon: /static/storage/content/MeshWeaver/Documentation/Northwind/icon.svg
---

# Unified References in Northwind

This document demonstrates unified references in the Northwind analytics sample. For the complete Unified Path syntax reference, see [Unified Path](MeshWeaver/Documentation/DataMesh/UnifiedPath).

It covers namespace hierarchy, data queries, and layout areas specific to the Northwind sample.

## Organization Structure

The Northwind sample follows this hierarchy:

```
Northwind/                           # Root namespace
├── AnalyticsCatalog.json             # NodeType definition
├── Analytics.json                   # Database instance
├── NorthwindAgent.md                # AI agent
├── Access/
│   └── Public.json                  # Access control
├── Data/
│   ├── orders.csv                   # Order data
│   └── orders_details.csv           # Order details
└── AnalyticsCatalog/Code/            # View implementations
    ├── Order.cs
    ├── OrderDetails.cs
    ├── Product.cs
    └── ...
```

## Namespace Paths

### Root Level

| Path | Description |
|------|-------------|
| `Northwind` | The root namespace |
| `Northwind/AnalyticsCatalog` | AnalyticsCatalog NodeType definition |
| `Northwind/Analytics` | Database instance |
| `Northwind/NorthwindAgent` | AI analytics agent |

### NodeType Definition

| Path | Description |
|------|-------------|
| `Northwind/AnalyticsCatalog` | AnalyticsCatalog NodeType with 53 views |

## Data References

See [Data Prefix](MeshWeaver/Documentation/DataMesh/UnifiedPath/DataPrefix) for the generic data reference syntax.

### Data Types

Northwind exposes these data types through the AnalyticsCatalog:

| Reference | Description |
|-----------|-------------|
| `@Northwind/data:Order` | All orders |
| `@Northwind/data:OrderDetails` | All order line items |
| `@Northwind/data:Product` | All products |
| `@Northwind/data:Customer` | All customers |
| `@Northwind/data:Employee` | All employees |
| `@Northwind/data:Supplier` | All suppliers |
| `@Northwind/data:Category` | Product categories |
| `@Northwind/data:NorthwindDataCube` | Virtual analytics cube |

### Specific Entity References

**Orders**:

| Reference | Description |
|-----------|-------------|
| `@Northwind/data:Order/10248` | Order #10248 |
| `@Northwind/data:Order/10249` | Order #10249 |

**Products**:

| Reference | Description |
|-----------|-------------|
| `@Northwind/data:Product/1` | Product ID 1 (Chai) |
| `@Northwind/data:Product/2` | Product ID 2 (Chang) |

**Customers**:

| Reference | Description |
|-----------|-------------|
| `@Northwind/data:Customer/ALFKI` | Alfreds Futterkiste |
| `@Northwind/data:Customer/QUICK` | QUICK-Stop |

## Layout Area References

See [Area Prefix](MeshWeaver/Documentation/DataMesh/UnifiedPath/AreaPrefix) for layout area syntax.

### Dashboard

Main overview dashboard:

```
@@Northwind/Dashboard
```

@@Northwind/Dashboard

---

### Sales Analytics

Revenue and sales performance views:

```
@@Northwind/SalesByCategory
```

@@Northwind/SalesByCategory

```
@@Northwind/SalesGrowthSummary
```

@@Northwind/SalesGrowthSummary

```
@@Northwind/SalesByCategoryComparison
```

@@Northwind/SalesByCategoryComparison

```
@@Northwind/SalesByCategoryWithPrevYear
```

@@Northwind/SalesByCategoryWithPrevYear

```
@@Northwind/CountrySalesComparison
```

@@Northwind/CountrySalesComparison

```
@@Northwind/RegionalAnalysis
```

@@Northwind/RegionalAnalysis

---

### Orders

Order analysis and reports:

```
@@Northwind/OrderSummary
```

@@Northwind/OrderSummary

```
@@Northwind/OrdersCount
```

@@Northwind/OrdersCount

```
@@Northwind/AvgOrderValue
```

@@Northwind/AvgOrderValue

```
@@Northwind/MonthlyOrdersTable
```

@@Northwind/MonthlyOrdersTable

```
@@Northwind/OrderDetailsReport
```

@@Northwind/OrderDetailsReport

```
@@Northwind/OrdersSummaryReport
```

@@Northwind/OrdersSummaryReport

```
@@Northwind/AvgOrderValueReport
```

@@Northwind/AvgOrderValueReport

```
@@Northwind/MonthlyAvgPricesTable
```

@@Northwind/MonthlyAvgPricesTable

---

### Products

Product analytics and performance:

```
@@Northwind/ProductOverview
```

@@Northwind/ProductOverview

```
@@Northwind/TopProducts
```

@@Northwind/TopProducts

```
@@Northwind/TopProductsByCategory
```

@@Northwind/TopProductsByCategory

```
@@Northwind/ProductCategoryAnalysis
```

@@Northwind/ProductCategoryAnalysis

```
@@Northwind/ProductSalesReport
```

@@Northwind/ProductSalesReport

```
@@Northwind/TopProductsByRevenue
```

@@Northwind/TopProductsByRevenue

```
@@Northwind/ProductPerformanceTrends
```

@@Northwind/ProductPerformanceTrends

```
@@Northwind/ProductDiscountImpact
```

@@Northwind/ProductDiscountImpact

```
@@Northwind/ProductSalesVelocity
```

@@Northwind/ProductSalesVelocity

---

### Customers

Customer analytics and segmentation:

```
@@Northwind/CustomerSummary
```

@@Northwind/CustomerSummary

```
@@Northwind/TopClients
```

@@Northwind/TopClients

```
@@Northwind/TopCustomersByRevenue
```

@@Northwind/TopCustomersByRevenue

```
@@Northwind/CustomerOrderFrequency
```

@@Northwind/CustomerOrderFrequency

```
@@Northwind/CustomerGeographicDistribution
```

@@Northwind/CustomerGeographicDistribution

```
@@Northwind/TopClientsTable
```

@@Northwind/TopClientsTable

```
@@Northwind/CustomerLifetimeValue
```

@@Northwind/CustomerLifetimeValue

```
@@Northwind/CustomerSegmentation
```

@@Northwind/CustomerSegmentation

```
@@Northwind/CustomerRetentionAnalysis
```

@@Northwind/CustomerRetentionAnalysis

```
@@Northwind/CustomerPurchaseBehavior
```

@@Northwind/CustomerPurchaseBehavior

```
@@Northwind/TopClientsRewardSuggestions
```

@@Northwind/TopClientsRewardSuggestions

---

### Employees

Employee performance metrics:

```
@@Northwind/EmployeeMetrics
```

@@Northwind/EmployeeMetrics

```
@@Northwind/TopEmployees
```

@@Northwind/TopEmployees

```
@@Northwind/TopEmployeesTable
```

@@Northwind/TopEmployeesTable

```
@@Northwind/TopEmployeesReport
```

@@Northwind/TopEmployeesReport

```
@@Northwind/TopEmployeesByRevenue
```

@@Northwind/TopEmployeesByRevenue

---

### Suppliers

Supplier analysis:

```
@@Northwind/SupplierSummary
```

@@Northwind/SupplierSummary

```
@@Northwind/SupplierAnalysis
```

@@Northwind/SupplierAnalysis

---

### Financial

Financial metrics and reports:

```
@@Northwind/FinancialSummary
```

@@Northwind/FinancialSummary

```
@@Northwind/RevenueSummary
```

@@Northwind/RevenueSummary

```
@@Northwind/DiscountSummary
```

@@Northwind/DiscountSummary

```
@@Northwind/DiscountVsRevenue
```

@@Northwind/DiscountVsRevenue

```
@@Northwind/MonthlyBreakdownTable
```

@@Northwind/MonthlyBreakdownTable

```
@@Northwind/DiscountPercentage
```

@@Northwind/DiscountPercentage

```
@@Northwind/DiscountAnalysisReport
```

@@Northwind/DiscountAnalysisReport

```
@@Northwind/DiscountEffectivenessReport
```

@@Northwind/DiscountEffectivenessReport

---

### Inventory

Inventory and trend analysis:

```
@@Northwind/StockLevelsAnalysis
```

@@Northwind/StockLevelsAnalysis

```
@@Northwind/MonthlySalesTrend
```

@@Northwind/MonthlySalesTrend

```
@@Northwind/QuarterlyPerformance
```

@@Northwind/QuarterlyPerformance

---

## Reference Data

### Product Categories

| ID | Category |
|----|----------|
| 1 | Beverages |
| 2 | Condiments |
| 3 | Confections |
| 4 | Dairy Products |
| 5 | Grains/Cereals |
| 6 | Meat/Poultry |
| 7 | Produce |
| 8 | Seafood |

### Regions

| ID | Region |
|----|--------|
| 1 | Eastern |
| 2 | Western |
| 3 | Northern |
| 4 | Southern |

### Sample Customers

| ID | Company |
|----|---------|
| ALFKI | Alfreds Futterkiste |
| QUICK | QUICK-Stop |
| ERNSH | Ernst Handel |
| SAVEA | Save-a-lot Markets |
| RATTC | Rattlesnake Canyon Grocery |

### Sample Products

| ID | Product | Category |
|----|---------|----------|
| 1 | Chai | Beverages |
| 2 | Chang | Beverages |
| 17 | Alice Mutton | Meat/Poultry |
| 38 | Côte de Blaye | Beverages |
| 56 | Gnocchi di nonna Alice | Grains/Cereals |

## Navigation Links

### Link to Database

```markdown
[Northwind Analytics](/Northwind/Analytics)
```

### Link to Specific View

```markdown
[Dashboard](/Northwind/Dashboard)
[Sales by Category](/Northwind/SalesByCategory)
[Top Customers](/Northwind/TopClients)
```

## Summary

The Northwind sample demonstrates MeshWeaver's unified path system for analytics:

- **Namespace paths** organize the analytics hierarchy
- **Data references** access orders, products, customers via `@path/data:Type`
- **Layout areas** display views via `@@path/ViewName`
- **53 views** across 8 categories for comprehensive analytics

All paths follow the pattern: `Northwind/[Instance]/[View]`
