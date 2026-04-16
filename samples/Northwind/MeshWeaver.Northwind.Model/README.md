# MeshWeaver.Northwind.Model

## Overview
MeshWeaver.Northwind.Model provides the data configuration layer for the Northwind sample application. It imports domain entities from embedded CSV files and defines address-based routing for Northwind services.

## Features
- Extension methods on `MessageHubConfiguration` to register each Northwind data set (customers, orders, products, employees, suppliers, reference data)
- Embedded CSV resources for all 11 Northwind tables (categories, customers, employees, orders, order details, products, regions, shippers, suppliers, territories, employee territories)
- `NorthwindAddresses` with typed address constants and factory methods for service routing
- `AddNorthwindDomain` helper for registering all domain types on a data source

## Usage
```csharp
configuration
    .AddNorthwindReferenceData()
    .AddNorthwindCustomers()
    .AddNorthwindOrders()
    .AddNorthwindProducts();
```

## Related Projects
- [MeshWeaver.Northwind.Domain](../MeshWeaver.Northwind.Domain/) -- Domain model records
- [MeshWeaver.Import](../../../src/MeshWeaver.Import/README.md) -- CSV import framework
- [MeshWeaver.Data](../../../src/MeshWeaver.Data/README.md) -- Data access framework
