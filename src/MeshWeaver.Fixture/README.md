# MeshWeaver.Fixture

MeshWeaver.Fixture provides the foundational testing infrastructure for the MeshWeaver ecosystem. It includes base classes and utilities that make it easier to write consistent, reliable tests, particularly for components that use message hubs.

## Overview

The library provides:
- Base test classes for different testing scenarios
- Message hub testing infrastructure
- Test utilities and helpers
- Common test configurations

## Core Components

### TestBase
The root base class for all MeshWeaver tests:

```csharp
public abstract class TestBase : IAsyncLifetime
{
    protected readonly ITestOutputHelper Output;
    
    protected TestBase(ITestOutputHelper output)
    {
        Output = output;
    }

    // Lifecycle methods
    public virtual Task InitializeAsync() => Task.CompletedTask;
    public virtual Task DisposeAsync() => Task.CompletedTask;
}
```

### HubTestBase
Base class for testing components that use message hubs:

```csharp
public class HubTestBase : TestBase
{
    protected IMessageHub Router { get; }
    protected IMessageHub Host { get; }
    
    protected virtual MessageHubConfiguration ConfigureRouter(MessageHubConfiguration configuration)
    {
        return configuration.WithTypes(typeof(PingRequest), typeof(PingResponse));
    }
    
    protected virtual MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return configuration.WithHandler<PingRequest>((hub, request) =>
        {
            hub.Post(new PingResponse(), options => options.ResponseFor(request));
            return request.Processed();
        });
    }
    
    // Helper methods for tests
    protected IMessageHub GetClient() => 
        Router.ServiceProvider.CreateMessageHub(new ClientAddress());
}
```

## Usage Examples

### Basic Test
```csharp
public class MyTest : TestBase
{
    public MyTest(ITestOutputHelper output) : base(output) { }
    
    [Fact]
    public async Task BasicTest()
    {
        // Test implementation
        await Task.CompletedTask;
    }
}
```

### Message Hub Test
```csharp
public class MyHubTest : HubTestBase
{
    public MyHubTest(ITestOutputHelper output) : base(output) { }
    
    protected override MessageHubConfiguration ConfigureHost(
        MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithHandler<CustomRequest>((hub, request) =>
            {
                hub.Post(new CustomResponse(), o => o.ResponseFor(request));
                return request.Processed();
            });
    }

    [Fact]
    public async Task RequestResponse()
    {
        var client = GetClient();
        var response = await client.AwaitResponse(
            new CustomRequest(),
            o => o.WithTarget(new HostAddress())
        );
        response.Should().BeOfType<CustomResponse>();
    }
}
```

### Testing with Multiple Hubs
```csharp
public class DistributedTest : HubTestBase
{
    protected override MessageHubConfiguration ConfigureRouter(
        MessageHubConfiguration configuration)
    {
        return base.ConfigureRouter(configuration)
            .WithRoutes(forward =>
                forward
                    .RouteAddressToHostedHub<DataAddress>(c => 
                        c.ConfigureDataHub())
                    .RouteAddressToHostedHub<ComputeAddress>(c => 
                        c.ConfigureComputeHub())
            );
    }

    [Fact]
    public async Task DistributedProcessing()
    {
        var client = GetClient();
        // Test distributed message processing
    }
}
```

## Features

1. **Test Base Classes**
   - `TestBase` - Root test class with lifecycle management
   - `HubTestBase` - For message hub testing
   - Custom base classes for specific scenarios

2. **Message Hub Testing**
   - Request-response testing
   - Message routing
   - Hub configuration
   - Client creation

3. **Test Utilities**
   - Output helpers
   - Async support
   - Common assertions
   - Test data generation

4. **Configuration**
   - Hub configuration helpers
   - Route configuration
   - Handler registration
   - Service configuration

## Best Practices

1. **Test Class Organization**
   ```csharp
   public class MyTests : HubTestBase
   {
       // Configuration overrides
       protected override MessageHubConfiguration ConfigureHost(
           MessageHubConfiguration configuration)
       {
           return base.ConfigureHost(configuration)
               .WithCustomConfiguration();
       }

       // Test methods
       [Fact]
       public async Task TestScenario()
       {
           // Arrange
           var client = GetClient();

           // Act
           var result = await ExecuteTest();

           // Assert
           result.Should().BeSuccessful();
       }
   }
   ```

2. **Async Testing**
   ```csharp
   [Fact]
   public async Task AsyncTest()
   {
       await using var client = GetClient();
       var result = await client
           .AwaitResponse(request)
           .Timeout(5.Seconds());
       
       result.Should().NotBeNull();
   }
   ```

3. **Hub Configuration**
   ```csharp
   protected override MessageHubConfiguration ConfigureHost(
       MessageHubConfiguration configuration)
   {
       return configuration
           .WithTypes(messageTypes)
           .WithHandlers(handlers)
           .WithServices(services);
   }
   ```

## Integration

### With xUnit
```csharp
// Fact attribute for hub tests
public class HubFactAttribute : FactAttribute
{
    public HubFactAttribute()
    {
        // Configure timeout and other test parameters
    }
}

// Using in tests
[HubFact]
public async Task MyHubTest()
{
    // Test implementation
}
```

## Related Projects

- MeshWeaver.Messaging.Hub - Core messaging functionality
- MeshWeaver.TestDomain - Test domain models
- MeshWeaver.Data.Test - Data module tests
