# MeshWeaver.Data

## Overview
MeshWeaver.Data provides data access and persistence capabilities for the MeshWeaver ecosystem. This library handles database interactions, data models, query operations, and data serialization to support the application's data management needs.

## Features
- Stream-based data access
- CRDT-based data synchronization
- CRUD operations on data entities
- Type-based data sources and collections
- JSON serialization and deserialization
- Documentation integration
- Reactive data streams with Observables

## Usage

### Configuring Data Sources

The MeshWeaver data system uses extension methods on `MessageHubConfiguration` to register data sources. Examples from the Northwind module show the configuration pattern:

```csharp

public static MessageHubConfiguration AddNorthwindCustomers(
        this MessageHubConfiguration configuration
    )
{
    return configuration
        .AddImport()
        .AddData(data =>
            data.FromEmbeddedResource(
                new EmbeddedResource(MyAssembly, "Files.customers.csv"),
                config => config.WithType<Customer>()
            )
        );
}

// Client code using these extension methods
services.AddMeshWeaverHub(hub => hub
    .AddNorthwindCustomers()
);
```

### Working with Data Models

MeshWeaver.Data works with plain C# records and classes. Properties can be decorated with data annotations:

```csharp
// From DataPluginTest.cs
public record MyData(string Id, [property:Required]string Text)
{
    public static MyData[] InitialData = [new("1", "A"), new("2", "B")];
}
```

### Reactive Data Streams

MeshWeaver.Data uses a reactive programming model with observables for data access and change tracking:

```csharp
// From DataPluginTest.cs - CheckUsagesFromWorkspaceVariable test
var clientWorkspace = GetClient().GetWorkspace();
    
// Subscribe to data changes using an observable
var data = await clientWorkspace
    .GetObservable<MyData>()
    .Timeout(10.Seconds())
    .FirstOrDefaultAsync();

data.Should().NotBeNull();
```

### Data Operations

Common data operations through the workspace:

```csharp
// From DataPluginTest.cs - Update test
// Get workspace and retrieve initial data
var clientWorkspace = client.GetWorkspace();
var data = (
    await clientWorkspace
        .GetObservable<MyData>()
        .Timeout(10.Seconds())
        .FirstOrDefaultAsync()
)
    .OrderBy(a => a.Id)
    .ToArray();

// Perform update with multiple items
var updateItems = new object[] { new MyData("1", "AAA"), new MyData("3", "CCC"), };
var updateResponse = await client.AwaitResponse(
    DataChangeRequest.Update(updateItems),
    o => o.WithTarget(new ClientAddress())
);

// Verify updated data
data = (
    await clientWorkspace
        .GetObservable<MyData>()
        .Timeout(10.Seconds())
        .FirstOrDefaultAsync(x => x?.Count == 3)
)
    .OrderBy(a => a.Id)
    .ToArray();
```

### Data Modelling and Validation
Data models are created as standard record types using XML comments to document.
MeshWeaver.Data supports validation using standard .NET data annotations from the System.ComponentModel.DataAnnotations namespace:

```csharp
using System.ComponentModel.DataAnnotations;

/// <summary>
/// Represents a product in the system with validation attributes.
/// </summary>
public record Product
{
    /// <summary>
    /// Gets or sets the unique identifier for the product.
    /// </summary>
    [Required]
    [StringLength(10, MinimumLength = 3)]
    public string Id { get; init; }

    /// <summary>
    /// Gets or sets the display name of the product.
    /// </summary>
    [Required(ErrorMessage = "Product name is required")]
    [StringLength(100)]
    public string Name { get; init; }

    /// <summary>
    /// Gets or sets the product description.
    /// </summary>
    [StringLength(500)]
    public string Description { get; init; }

    /// <summary>
    /// Gets or sets the price of the product.
    /// </summary>
    [Range(0.01, 10000.00, ErrorMessage = "Price must be between $0.01 and $10,000.00")]
    public decimal Price { get; init; }

    /// <summary>
    /// Gets or sets the product category.
    /// </summary>
    [Required]
    public string Category { get; init; }

    /// <summary>
    /// Gets or sets the product inventory count.
    /// </summary>
    [Range(0, 1000)]
    public int StockQuantity { get; init; }

}
```

### Remote Stream Access

MeshWeaver.Data allows accessing data streams from remote sources using the GetRemoteStream() method. This is particularly useful for accessing data from specific domains or partitioned data sources:

```csharp
// Access a remote stream of data
// This is typically used in domain service tests and layout service tests
var remoteStream = workspace.GetRemoteStream<EntityType>(domainAddress);

// Process the remote stream data
await remoteStream
    .Timeout(TimeSpan.FromSeconds(10))
    .Select(entities => ProcessEntities(entities))
    .Subscribe(
        result => Logger.LogInformation($"Processed {result.Count} entities"),
        error => Logger.LogError($"Error processing entities: {error.Message}")
    );
```

The GetRemoteStream() method is essential for distributed data scenarios where data lives across different domains or partitions in the MeshWeaver ecosystem.

#### Updating Remote Streams

Remote streams can be updated using the Update method with a lambda expression that modifies the current state.

The Update method accepts a lambda function that:
1. Receives the current state of the data
2. Allows you to make modifications to that state
3. Returns the updated state

This pattern enables maintaining consistency across distributed systems while allowing for complex state modifications.

## Key Components
- **DataContext**: Central container for data sources and type sources
- **TypeSource**: Repository for accessing typed entities
- **IDataStorage**: Interface for persistence providers
- **InstanceCollection**: Storage for entity instances with change tracking
- **GetObservable<T>()**: Returns a reactive stream of data entities
- **DataChangeRequest**: Commands for CRUD operations on data

## Related Projects
- [MeshWeaver.Data.Contract](../MeshWeaver.Data.Contract/README.md) - Interface definitions
- [MeshWeaver.Domain](../MeshWeaver.Domain/README.md) - Domain model definitions
- [Northwind](../../modules/Northwind/README.md) - Reference implementation

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall project. 