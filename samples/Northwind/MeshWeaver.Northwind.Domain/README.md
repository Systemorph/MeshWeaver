# MeshWeaver.Northwind.Domain

## Overview
MeshWeaver.Northwind.Domain defines the data model for the classic Northwind sample database. It provides record types for all core business entities used across Northwind sample applications and tests.

## Domain Types

**Operational types:** `Order`, `OrderDetails`, `Customer`, `Employee`, `Product`, `Supplier`

**Reference data types:** `Category`, `Region`, `Territory`

All types are plain C# records with data annotations, dimension attributes for cross-references, and XML documentation. They implement `INamed` where appropriate.

## Usage
```csharp
// Access all domain types programmatically
var allTypes = NorthwindDomain.OperationalTypes
    .Concat(NorthwindDomain.ReferenceDataTypes);
```

## Related Projects
- [MeshWeaver.Northwind.Model](../MeshWeaver.Northwind.Model/) -- Data configuration and CSV import
- [MeshWeaver.Data](../../../src/MeshWeaver.Data/README.md) -- Data access framework
