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
@data:app/Northwind/Order
```

@data:app/Northwind/Order

### Single Order

Display a specific order:

```
@data:app/Northwind/Order/10248
```

@data:app/Northwind/Order/10248

### Order Details Collection

Display all order details:

```
@data:app/Northwind/OrderDetails
```

@data:app/Northwind/OrderDetails

### Customer Collection

Display all customers:

```
@data:app/Northwind/Customer
```

@data:app/Northwind/Customer

### Single Customer

Display a specific customer:

```
@data:app/Northwind/Customer/ALFKI
```

@data:app/Northwind/Customer/ALFKI

### Product Collection

Display all products:

```
@data:app/Northwind/Product
```

@data:app/Northwind/Product

### Single Product

Display a specific product:

```
@data:app/Northwind/Product/1
```

@data:app/Northwind/Product/1

### Employee Collection

Display all employees:

```
@data:app/Northwind/Employee
```

@data:app/Northwind/Employee

### Single Employee

Display a specific employee:

```
@data:app/Northwind/Employee/1
```

@data:app/Northwind/Employee/1

### Supplier Collection

Display all suppliers:

```
@data:app/Northwind/Supplier
```

@data:app/Northwind/Supplier

### Single Supplier

Display a specific supplier:

```
@data:app/Northwind/Supplier/1
```

@data:app/Northwind/Supplier/1

## Reference Data

### Category Collection

Display all categories:

```
@data:app/Northwind/Category
```

@data:app/Northwind/Category

### Single Category

Display a specific category:

```
@data:app/Northwind/Category/1
```

@data:app/Northwind/Category/1

### Region Collection

Display all regions:

```
@data:app/Northwind/Region
```

@data:app/Northwind/Region

### Single Region

Display a specific region:

```
@data:app/Northwind/Region/1
```

@data:app/Northwind/Region/1

### Territory Collection

Display all territories:

```
@data:app/Northwind/Territory
```

@data:app/Northwind/Territory

### Single Territory

Display a specific territory:

```
@data:app/Northwind/Territory/01581
```

@data:app/Northwind/Territory/01581

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

