---
Title: "Northwind Unified References"
Abstract: >
  This document demonstrates all addressable items in the Northwind application using MeshWeaver's
  unified content reference notation. It covers operational data, reference data, and layout areas
  specific to the Northwind domain.
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
@app/Northwind/data/Order
```

@app/Northwind/data/Order

### Single Order

Display a specific order:

```
@app/Northwind/data/Order/10248
```

@app/Northwind/data/Order/10248

### Order Details Collection

Display all order details:

```
@app/Northwind/data/OrderDetails
```

@app/Northwind/data/OrderDetails

### Customer Collection

Display all customers:

```
@app/Northwind/data/Customer
```

@app/Northwind/data/Customer

### Single Customer

Display a specific customer:

```
@app/Northwind/data/Customer/ALFKI
```

@app/Northwind/data/Customer/ALFKI

### Product Collection

Display all products:

```
@app/Northwind/data/Product
```

@app/Northwind/data/Product

### Single Product

Display a specific product:

```
@app/Northwind/data/Product/1
```

@app/Northwind/data/Product/1

### Employee Collection

Display all employees:

```
@app/Northwind/data/Employee
```

@app/Northwind/data/Employee

### Single Employee

Display a specific employee:

```
@app/Northwind/data/Employee/1
```

@app/Northwind/data/Employee/1

### Supplier Collection

Display all suppliers:

```
@app/Northwind/data/Supplier
```

@app/Northwind/data/Supplier

### Single Supplier

Display a specific supplier:

```
@app/Northwind/data/Supplier/1
```

@app/Northwind/data/Supplier/1

## Reference Data

### Category Collection

Display all categories:

```
@app/Northwind/data/Category
```

@app/Northwind/data/Category

### Single Category

Display a specific category:

```
@app/Northwind/data/Category/1
```

@app/Northwind/data/Category/1

### Region Collection

Display all regions:

```
@app/Northwind/data/Region
```

@app/Northwind/data/Region

### Single Region

Display a specific region:

```
@app/Northwind/data/Region/1
```

@app/Northwind/data/Region/1

### Territory Collection

Display all territories:

```
@app/Northwind/data/Territory
```

@app/Northwind/data/Territory

### Single Territory

Display a specific territory:

```
@app/Northwind/data/Territory/01581
```

@app/Northwind/data/Territory/01581

## Layout Area References

### Main Dashboard

The main Northwind dashboard with business metrics:

```
@app/Northwind/Dashboard
```

@app/Northwind/Dashboard

### Annual Report Summary

Annual business performance summary:

```
@app/Northwind/AnnualReportSummary
```

@app/Northwind/AnnualReportSummary

### Orders Views

#### Orders Summary

Top orders summary:

```
@app/Northwind/OrderSummary
```

@app/Northwind/OrderSummary

#### Orders Overview

Orders count and average value:

```
@app/Northwind/OrdersCount
```

@app/Northwind/OrdersCount

```
@app/Northwind/AvgOrderValue
```

@app/Northwind/AvgOrderValue

#### Orders Analysis

Detailed orders analysis:

```
@app/Northwind/OrdersSummaryReport
```

@app/Northwind/OrdersSummaryReport

```
@app/Northwind/MonthlyOrdersTable
```

@app/Northwind/MonthlyOrdersTable

### Customer Views

#### Customer Summary

```
@app/Northwind/CustomerSummary
```

@app/Northwind/CustomerSummary

#### Customer Analysis

```
@app/Northwind/TopCustomersByRevenue
```

@app/Northwind/TopCustomersByRevenue

```
@app/Northwind/CustomerLifetimeValue
```

@app/Northwind/CustomerLifetimeValue

```
@app/Northwind/CustomerSegmentation
```

@app/Northwind/CustomerSegmentation

### Product Views

#### Product Overview

```
@app/Northwind/ProductOverview
```

@app/Northwind/ProductOverview

#### Product Analysis

```
@app/Northwind/TopProductsByRevenue
```

@app/Northwind/TopProductsByRevenue

```
@app/Northwind/ProductPerformanceTrends
```

@app/Northwind/ProductPerformanceTrends

```
@app/Northwind/TopProducts
```

@app/Northwind/TopProducts

```
@app/Northwind/TopProductsByCategory
```

@app/Northwind/TopProductsByCategory

### Sales Views

#### Sales by Category

```
@app/Northwind/SalesByCategory
```

@app/Northwind/SalesByCategory

```
@app/Northwind/SalesByCategoryComparison
```

@app/Northwind/SalesByCategoryComparison

```
@app/Northwind/SalesInOneCategory
```

@app/Northwind/SalesInOneCategory

#### Sales Geography

```
@app/Northwind/CountrySalesComparison
```

@app/Northwind/CountrySalesComparison

```
@app/Northwind/RegionalAnalysis
```

@app/Northwind/RegionalAnalysis

#### Sales Growth

```
@app/Northwind/SalesGrowthSummary
```

@app/Northwind/SalesGrowthSummary

### Employee Views

#### Employee Performance

```
@app/Northwind/TopEmployeesByRevenue
```

@app/Northwind/TopEmployeesByRevenue

```
@app/Northwind/EmployeeMetrics
```

@app/Northwind/EmployeeMetrics

```
@app/Northwind/TopEmployees
```

@app/Northwind/TopEmployees

### Supplier Views

```
@app/Northwind/SupplierSummary
```

@app/Northwind/SupplierSummary

```
@app/Northwind/SupplierAnalysis
```

@app/Northwind/SupplierAnalysis

### Financial Views

#### Financial Summary

```
@app/Northwind/FinancialSummary
```

@app/Northwind/FinancialSummary

```
@app/Northwind/RevenueSummary
```

@app/Northwind/RevenueSummary

### Discount Analysis

```
@app/Northwind/DiscountSummary
```

@app/Northwind/DiscountSummary

```
@app/Northwind/DiscountPercentage
```

@app/Northwind/DiscountPercentage

```
@app/Northwind/DiscountVsRevenue
```

@app/Northwind/DiscountVsRevenue

### Inventory Analysis

```
@app/Northwind/StockLevelsAnalysis
```

@app/Northwind/StockLevelsAnalysis

### Time Series Analysis

```
@app/Northwind/MonthlySalesTrend
```

@app/Northwind/MonthlySalesTrend

```
@app/Northwind/QuarterlyPerformance
```

@app/Northwind/QuarterlyPerformance

### Top Clients

```
@app/Northwind/TopClients
```

@app/Northwind/TopClients

```
@app/Northwind/TopClientsTable
```

@app/Northwind/TopClientsTable

### Reports

#### Detailed Reports

```
@app/Northwind/OrderDetailsReport
```

@app/Northwind/OrderDetailsReport

```
@app/Northwind/ProductSalesReport
```

@app/Northwind/ProductSalesReport

