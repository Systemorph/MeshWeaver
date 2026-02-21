---
Title: "Northwind Unified References"
Abstract: >
  This document demonstrates all addressable items in the Northwind application using MeshWeaver's
  unified content reference notation. It covers operational data, reference data, and layout areas
  specific to the Northwind domain.
Icon: "Document"
Thumbnail: "images/UnifiedReferences.svg"
Published: "2025-12-06"
Authors:
  - "Roland Buergi"
Tags:
  - "Northwind"
  - "Data"
  - "Layout Areas"
---

This document showcases all addressable items in the Northwind application.

## Operational Data

### Order Collection

Display all orders:

```
@@("Northwind/Analytics/$Data/Order")
```

@@("Northwind/Analytics/$Data/Order")

### Single Order

Display a specific order:

```
@@("Northwind/Analytics/$Data/Order/10248")
```

@@("Northwind/Analytics/$Data/Order/10248")

### Order Details Collection

Display all order details:

```
@@("Northwind/Analytics/$Data/OrderDetails")
```

@@("Northwind/Analytics/$Data/OrderDetails")

### Customer Collection

Display all customers:

```
@@("Northwind/Analytics/$Data/Customer")
```

@@("Northwind/Analytics/$Data/Customer")

### Single Customer

Display a specific customer:

```
@@("Northwind/Analytics/$Data/Customer/ALFKI")
```

@@("Northwind/Analytics/$Data/Customer/ALFKI")

### Product Collection

Display all products:

```
@@("Northwind/Analytics/$Data/Product")
```

@@("Northwind/Analytics/$Data/Product")

### Single Product

Display a specific product:

```
@@("Northwind/Analytics/$Data/Product/1")
```

@@("Northwind/Analytics/$Data/Product/1")

### Employee Collection

Display all employees:

```
@@("Northwind/Analytics/$Data/Employee")
```

@@("Northwind/Analytics/$Data/Employee")

### Single Employee

Display a specific employee:

```
@@("Northwind/Analytics/$Data/Employee/1")
```

@@("Northwind/Analytics/$Data/Employee/1")

### Supplier Collection

Display all suppliers:

```
@@("Northwind/Analytics/$Data/Supplier")
```

@@("Northwind/Analytics/$Data/Supplier")

### Single Supplier

Display a specific supplier:

```
@@("Northwind/Analytics/$Data/Supplier/1")
```

@@("Northwind/Analytics/$Data/Supplier/1")

## Reference Data

### Category Collection

Display all categories:

```
@@("Northwind/Analytics/$Data/Category")
```

@@("Northwind/Analytics/$Data/Category")

### Single Category

Display a specific category:

```
@@("Northwind/Analytics/$Data/Category/1")
```

@@("Northwind/Analytics/$Data/Category/1")

### Region Collection

Display all regions:

```
@@("Northwind/Analytics/$Data/Region")
```

@@("Northwind/Analytics/$Data/Region")

### Single Region

Display a specific region:

```
@@("Northwind/Analytics/$Data/Region/1")
```

@@("Northwind/Analytics/$Data/Region/1")

### Territory Collection

Display all territories:

```
@@("Northwind/Analytics/$Data/Territory")
```

@@("Northwind/Analytics/$Data/Territory")

### Single Territory

Display a specific territory:

```
@@("Northwind/Analytics/$Data/Territory/01581")
```

@@("Northwind/Analytics/$Data/Territory/01581")

## Layout Area References

### Main Dashboard

The main Northwind dashboard with business metrics:

```
@@("Northwind/Analytics/Dashboard")
```

@@("Northwind/Analytics/Dashboard")

### Annual Report Summary

Annual business performance summary:

```
@@("Northwind/Analytics/AnnualReportSummary")
```

@@("Northwind/Analytics/AnnualReportSummary")

### Orders Views

#### Orders Summary

Top orders summary:

```
@@("Northwind/Analytics/OrderSummary")
```

@@("Northwind/Analytics/OrderSummary")

#### Orders Overview

Orders count and average value:

```
@@("Northwind/Analytics/OrdersCount")
```

@@("Northwind/Analytics/OrdersCount")

```
@@("Northwind/Analytics/AvgOrderValue")
```

@@("Northwind/Analytics/AvgOrderValue")

#### Orders Analysis

Detailed orders analysis:

```
@@("Northwind/Analytics/OrdersSummaryReport")
```

@@("Northwind/Analytics/OrdersSummaryReport")

```
@@("Northwind/Analytics/MonthlyOrdersTable")
```

@@("Northwind/Analytics/MonthlyOrdersTable")

### Customer Views

#### Customer Summary

```
@@("Northwind/Analytics/CustomerSummary")
```

@@("Northwind/Analytics/CustomerSummary")

#### Customer Analysis

```
@@("Northwind/Analytics/TopCustomersByRevenue")
```

@@("Northwind/Analytics/TopCustomersByRevenue")

```
@@("Northwind/Analytics/CustomerLifetimeValue")
```

@@("Northwind/Analytics/CustomerLifetimeValue")

```
@@("Northwind/Analytics/CustomerSegmentation")
```

@@("Northwind/Analytics/CustomerSegmentation")

### Product Views

#### Product Overview

```
@@("Northwind/Analytics/ProductOverview")
```

@@("Northwind/Analytics/ProductOverview")

#### Product Analysis

```
@@("Northwind/Analytics/TopProductsByRevenue")
```

@@("Northwind/Analytics/TopProductsByRevenue")

```
@@("Northwind/Analytics/ProductPerformanceTrends")
```

@@("Northwind/Analytics/ProductPerformanceTrends")

```
@@("Northwind/Analytics/TopProducts")
```

@@("Northwind/Analytics/TopProducts")

```
@@("Northwind/Analytics/TopProductsByCategory")
```

@@("Northwind/Analytics/TopProductsByCategory")

### Sales Views

#### Sales by Category

```
@@("Northwind/Analytics/SalesByCategory")
```

@@("Northwind/Analytics/SalesByCategory")

```
@@("Northwind/Analytics/SalesByCategoryComparison")
```

@@("Northwind/Analytics/SalesByCategoryComparison")

```
@@("Northwind/Analytics/SalesInOneCategory")
```

@@("Northwind/Analytics/SalesInOneCategory")

#### Sales Geography

```
@@("Northwind/Analytics/CountrySalesComparison")
```

@@("Northwind/Analytics/CountrySalesComparison")

```
@@("Northwind/Analytics/RegionalAnalysis")
```

@@("Northwind/Analytics/RegionalAnalysis")

#### Sales Growth

```
@@("Northwind/Analytics/SalesGrowthSummary")
```

@@("Northwind/Analytics/SalesGrowthSummary")

### Employee Views

#### Employee Performance

```
@@("Northwind/Analytics/TopEmployeesByRevenue")
```

@@("Northwind/Analytics/TopEmployeesByRevenue")

```
@@("Northwind/Analytics/EmployeeMetrics")
```

@@("Northwind/Analytics/EmployeeMetrics")

```
@@("Northwind/Analytics/TopEmployees")
```

@@("Northwind/Analytics/TopEmployees")

### Supplier Views

```
@@("Northwind/Analytics/SupplierSummary")
```

@@("Northwind/Analytics/SupplierSummary")

```
@@("Northwind/Analytics/SupplierAnalysis")
```

@@("Northwind/Analytics/SupplierAnalysis")

### Financial Views

#### Financial Summary

```
@@("Northwind/Analytics/FinancialSummary")
```

@@("Northwind/Analytics/FinancialSummary")

```
@@("Northwind/Analytics/RevenueSummary")
```

@@("Northwind/Analytics/RevenueSummary")

### Discount Analysis

```
@@("Northwind/Analytics/DiscountSummary")
```

@@("Northwind/Analytics/DiscountSummary")

```
@@("Northwind/Analytics/DiscountPercentage")
```

@@("Northwind/Analytics/DiscountPercentage")

```
@@("Northwind/Analytics/DiscountVsRevenue")
```

@@("Northwind/Analytics/DiscountVsRevenue")

### Inventory Analysis

```
@@("Northwind/Analytics/StockLevelsAnalysis")
```

@@("Northwind/Analytics/StockLevelsAnalysis")

### Time Series Analysis

```
@@("Northwind/Analytics/MonthlySalesTrend")
```

@@("Northwind/Analytics/MonthlySalesTrend")

```
@@("Northwind/Analytics/QuarterlyPerformance")
```

@@("Northwind/Analytics/QuarterlyPerformance")

### Top Clients

```
@@("Northwind/Analytics/TopClients")
```

@@("Northwind/Analytics/TopClients")

```
@@("Northwind/Analytics/TopClientsTable")
```

@@("Northwind/Analytics/TopClientsTable")

### Reports

#### Detailed Reports

```
@@("Northwind/Analytics/OrderDetailsReport")
```

@@("Northwind/Analytics/OrderDetailsReport")

```
@@("Northwind/Analytics/ProductSalesReport")
```

@@("Northwind/Analytics/ProductSalesReport")
