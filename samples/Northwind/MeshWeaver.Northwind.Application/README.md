# Northwind

## Overview
The Northwind module serves as a primary data provider for the MeshWeaver ecosystem, offering a comprehensive implementation of the classic Northwind database schema. This module provides sample data entities, relationships, and configuration, making it an excellent reference implementation for MeshWeaver data capabilities.

## Features
- Complete Northwind database schema implementation
- Sample data entities (Customers, Orders, Products, etc.)
- Data relationships and navigation
- Configurable data sources via extension methods
- Integration with MeshWeaver's data pipeline
- Reference implementation for data modeling
- UI components for displaying Northwind data
- Example business logic and validation rules

## Data Configuration

The NorthwindDataConfiguration class provides the main integration point between the Northwind data model and the MeshWeaver ecosystem:

```csharp
// Configure Northwind data in your application
services.AddMeshWeaverHub(hub => hub
    .AddNorthwindReferenceData()    // Categories, Regions, Territories, etc.
    .AddNorthwindCustomers()        // Customers data
    .AddNorthwindProducts()         // Products data
    .AddNorthwindSuppliers()        // Suppliers data
    .AddNorthwindEmployees()        // Employees data
    .AddNorthwindOrders()           // Orders, Order Details
);

// Access Northwind data in your application
public class NorthwindService
{
    private readonly IMessageHub _hub;
    
    public NorthwindService(IServiceProvider serviceProvider)
    {
        // Get customer-specific hub
        _hub = serviceProvider.GetCustomerHub(new CustomerAddress("ALFKI"));
    }
    
    public async Task<Customer> GetCustomerAsync(string customerId)
    {
        var workspace = _hub.GetWorkspace();
        var dataContext = workspace.GetDataContext();
        
        var customerSource = dataContext.GetDataSourceForType(typeof(Customer));
        return await customerSource.GetByIdAsync<Customer>(customerId);
    }
}
```

## Northwind Entities

The module provides these main entity types:
- **Customers**: Company information and contact details
- **Products**: Product catalog with pricing and stock information
- **Orders**: Customer orders with shipping details
- **Order Details**: Line items for each order
- **Employees**: Staff information with reporting hierarchy
- **Suppliers**: Product vendors and contact information
- **Categories**: Product categorization
- **Shippers**: Companies that ship the orders
- **Regions/Territories**: Geographic information

## Integration with MeshWeaver

The Northwind module demonstrates several key MeshWeaver data patterns:
- Configuring data sources with MessageHubConfiguration
- Working with entity relationships
- Implementing data access patterns
- Creating user interfaces for data visualization
- Building data-driven business rules
- Testing data operations

## Example Use Cases
- Data grid displays of Northwind entities
- Order processing workflows
- Product catalog browsing
- Customer management
- Sales reporting and analytics
- Data relationship visualization

## Related Modules
- [MeshWeaver.Data](../../src/MeshWeaver.Data/README.md) - Core data functionality
- [MeshWeaver.DataCubes](../../src/MeshWeaver.DataCubes/README.md) - Data analytics
- [Documentation](../Documentation/README.md) - Module documentation

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall project.
