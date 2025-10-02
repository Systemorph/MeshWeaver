# AGENTS.md

This file provides guidance to AI agents when working with code in this repository.

## Development Commands

### Build and Test
```bash
# Build entire solution
dotnet build

# Run tests (uses xUnit v3)
dotnet test

# Run specific test project (example)
dotnet test test/MeshWeaver.Data.Test/MeshWeaver.Data.Test.csproj

# Clean solution
dotnet clean

# Restore packages
dotnet restore
```

### Running Applications

#### Monolithic Portal (Development)
```bash
cd portal/MeshWeaver.Portal
dotnet run
# Access at https://localhost:65260
```

#### Microservices Portal (.NET Aspire)
```bash
cd portal/aspire/MeshWeaver.Portal.AppHost
dotnet run
# Access Aspire dashboard for service management
# Requires Docker for dependencies
```

### Project Templates
```bash
# Install MeshWeaver project templates
dotnet new install MeshWeaver.ProjectTemplates

# Create new solution from template  
dotnet new meshweaver-solution -n MyApp
```

## Architecture Overview

### Core Concepts

**Message Hub Architecture**: MeshWeaver is built on an actor-model message hub system (`MeshWeaver.Messaging.Hub`). All application interactions flow through hierarchical message routing with address-based partitioning (e.g., `@app/Address/AreaName`).

**Layout Areas**: The UI system uses reactive Layout Areas - framework-agnostic UI abstractions that render in Blazor Server. Layout areas are addressed by route and automatically update via reactive streams.

**AI-First Design**: First-class AI integration using Semantic Kernel with plugins (DataPlugin, LayoutAreaPlugin, ChatPlugin) that provide agents access to application state and functionality.

### Key Directory Structure

- **`src/`** - Core framework libraries (50+ projects)
  - `MeshWeaver.Messaging.Hub` - Actor-based message routing
  - `MeshWeaver.Layout` - Framework-agnostic UI abstractions  
  - `MeshWeaver.AI` - Agent framework with plugin architecture
  - `MeshWeaver.Blazor` - Blazor Server implementation
  - `MeshWeaver.Data` - CRUD operations with activity tracking

- **`modules/`** - Business domain modules
  - `Documentation/` - Interactive markdown with live code execution
  - `Northwind/` - Sample business application with analytics
  - `Todo/` - Simple CRUD demonstration

- **`portal/`** - Web applications
  - `MeshWeaver.Portal/` - Monolithic deployment
  - `aspire/` - Microservices with .NET Aspire orchestration

### Architectural Patterns

**Request-Response**: Use `hub.AwaitResponse<TResponse>(request, o => o.WithTarget(address))` for operations requiring results. 
The response is submitted as `hub.Post(responseMessage, o => o.ResponseFor(request))`.

**Fire-and-Forget**: Use `hub.Post(message, o => o.WithTarget(address))` for notifications and events.

**Address-Based Routing**: Services register at specific addresses (e.g., `bookings/q1_2025`, `app/northwind`, `pricing/id`). 
Layout areas follow the pattern `@{address}/{areaName}/{areaId}`. The areaId is optional and depends on the view.
E.g. `{address}/Details/{itemId}` would render a details view for the item with `idemId`.

Layout areas are typically kept on the same address as the underlying data.

**Reactive UI**: All UI state changes flow through the message hub. Controls are immutable records that specify their current state.

## Development Patterns

### Adding New Layout Areas
```csharp
public static class MyLayoutArea
{
    public static void AddMyLayoutArea(this LayoutConfiguration config) =>
        config.AddLayoutArea(nameof(MyLayout), MyLayout);

    public static UiControl MyLayout(LayoutAreaHost host, RenderingContext ctx) => 
    Controls.Stack
            .WithView(Controls.Html("Some text")
            .WithView(Controls.Markdown("Some markdown view"))
    );

}
```
We support rich markdown with mermaid diagrams, code blocks, MathJax, 
and live execution via dynamic markdown. Layout areas can be inserted by
using `@{address}/{areaName}/{areaId}`

### Message Handling
Messages are registered in the configuration of the hub. Also DI is set up on the level of hub configuration:
```csharp
public static class NorthwindHubConfiguration
{
    public static MessageHubConfiguration AddNorthwindHub(this MessageHubConfiguration config)
    {
        return config.AddHandler<MyRequestAsync>(HandleMyRequestAsync)
                     .AddHandler<MyRequest>(HandleMyRequest);
        
    }

    public static async Task<IMessageDelivery> HandleMyRequestAsync(MessageHub hub, IMessageDelivery<MyRequestAsync> request, CancellationToken ct)
    {
        // Process the request
        var result = await SomeService.ProcessAsync(request.Message);
        
        // Send response
        await hub.Post(new MyResponse(result), o => o.ResponseFor(request));
        return request.Processed();
    }

    public static IMessageDelivery HandleMyRequest(MessageHub hub, IMessageDelivery<MyRequest> request)
    {
        // Process the request
        var result = SomeService.Process(request.Input);
        
        // Send response
        await hub.Post(new MyResponse(result), o => o.ResponseFor(request));
        return request.Processed();
    }
}
```

### AI Plugin Development
```csharp
public class MyPlugin(IMessageHub hub, IAgentChat chat)
{
    [KernelFunction]
    [Description("Description on how to use")]
    public async Task<string> DoSomething([Description("Description for input")]string input)
    {
        var request = new MyRequest(input); // Create a request object
        var address = GetAddress(request); // Get the address for the plugin, e.g., "app/northwind"
        // Use the message hub to send a request and receive a response
        var response = await hub.AwaitResponse<MyResponse>(request, o => o.WithTarget(address));
        return JsonSerializer.Serialize(response.Message, hub.JsonSerializationOptions);
    }

    public Address GetAddress(MyRequest request)
    {
        // Logic to determine the address based on the request
        // the chat contains a context, which is usually good to use.
        // can also contain agent specific mapping logic.
        return chat.Context.Address;
    }
}
```

## Key Dependencies

- **.NET 9.0** - Target framework
- **Orleans** - Distributed deployment (distributed deployment, microservices)
- **Blazor Server** - Web UI framework  
- **Semantic Kernel** - AI integration
- **xUnit v3** - Testing framework
- **FluentAssertions** - Test assertions
- **Chart.js** - Data visualization
- **Azure SDKs** - Cloud integration
- **Markdig** - Markdown processing


## Testing Guidelines

Tests use xUnit v3 with structured logging and test parallelization configured via `xunit.runner.json`:
- `parallelizeAssembly: false`
- `parallelizeTestCollections: false` 
- `maxParallelThreads: 1`
- `methodTimeout: 30000ms`

Use `MeshWeaver.Fixture` for test infrastructure:

```csharp
public class MyTest : HubTestBase, IAsyncLifetime
{

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration config)
    {
        return base.ConfigureHost(config)
            .AddNorthwindHub() // Register Northwind hub
            .WithSomeService(); // Add any required services
    }
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration config)
    {
        return base.ConfigureClient(config)
            .AddLayoutClient(); // Add any required services
    }
    
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await InitializeSomething();
    }
    
    public override async ValueTask DisposeAsync()
    {
        await DisposeSomething();
        await base.DisposeAsync;
    }

    [Fact]
    public async Task MyTestMethod()
    {
        // Arrange
        var request = new MyRequest("test input");
        var hub = GetClient();

        // Act
        var response = await hub.AwaitResponse<MyResponse>(request, o => o.WithTarget(new HostAddress()));
        // Assert
        response.Should().NotBeNull();
        response.Message.Result.Should().Be("expected result");
    }
}
```

## Project Structure Guidelines

- Framework code belongs in `src/`
- Test code belongs in `test/`
- Business modules go in `modules/` 
- Each module should have its own set of hubs and address spaces (e.g., `@app/northwind`)
- UI components should be framework-agnostic in the layout layer. The language are the controls inheriting from `UiControl`.
- AI agents should use plugins to access application functionality

## Solution Management

The solution uses centralized package management via `Directory.Packages.props`. When adding new dependencies, update the central package file rather than individual project files.

### Key Configuration Files
- `Directory.Build.props` - Global MSBuild properties and versioning
- `Directory.Packages.props` - Centralized NuGet package version management  
- `nuget.config` - NuGet package sources configuration
- `xunit.runner.json` - Test execution configuration

### Branch and Development
- Main branch: `main` (use for PRs)
- Solution file: `MeshWeaver.sln` contains 50+ projects