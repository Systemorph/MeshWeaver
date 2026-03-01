# AGENTS.md

This file provides guidance to AI agents when working with code in this repository.

## Documentation

Documentation is embedded in `src/MeshWeaver.Documentation/` and served under the `Doc/` namespace at runtime.

### Architecture

The documentation on the architecture is accessible via src/MeshWeaver.Documentation/Data/Architecture/

Topics: Message-based communication, Actor model, UI streaming, AI agents, Data versioning, Serialization, Access control, Partitioned persistence

### DataMesh

The documentation on the data mesh is accessible via src/MeshWeaver.Documentation/Data/DataMesh/

Topics: Node type configuration, Query syntax, Unified Path references, Interactive markdown, Collaborative editing, CRUD operations, Data modeling

### GUI

The documentation on the GUI is accessible via src/MeshWeaver.Documentation/Data/GUI/

Topics: Container controls (Stack, Tabs, Toolbar, Splitter), Layout grid, DataGrid, Editor, Observables, Data binding, Attributes, Reactive dialogs

### AI Integration

The documentation on AI integration is accessible via src/MeshWeaver.Documentation/Data/AI/

Topics: Agentic AI, MCP authentication, MeshPlugin tools (Get, Search, Create, Update, Delete, NavigateTo)

### Agents

Built-in agent definitions are embedded in src/MeshWeaver.AI/Data/Agent/

Agents: Executor, Navigator, Planner, Research

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

#### Memex Portal (Recommended for Development)
```bash
cd memex/Memex.Portal.Monolith
dotnet run
# Access at https://localhost:7122
```

The Memex Portal uses `AddGraph()` to dynamically load Graph nodes from `samples/Graph/Data/`, and `AddDocumentation()` to serve embedded documentation under the `Doc/` namespace. This is the recommended portal for development.

#### Microservices Portal (.NET Aspire)
```bash
cd memex/aspire/Memex.AppHost
dotnet run
# Access Aspire dashboard for service management
# Requires Docker for dependencies
```

## Architecture Overview

### Core Concepts

**Message Hub Architecture**: MeshWeaver is built on an actor-model message hub system (`MeshWeaver.Messaging.Hub`). All application interactions flow through hierarchical message routing with address-based partitioning (e.g., `@app/Address/AreaName`).

**Layout Areas**: The UI system uses reactive Layout Areas - framework-agnostic UI abstractions that render in Blazor Server. Layout areas are addressed by route and automatically update via reactive streams.

**AI-First Design**: First-class AI integration using Microsoft.Extensions.AI with plugins (MeshPlugin, LayoutAreaPlugin) that provide agents access to application state and functionality.

### Key Directory Structure

- **`src/`** - Core framework libraries (50+ projects)
  - `MeshWeaver.Messaging.Hub` - Actor-based message routing
  - `MeshWeaver.Layout` - Framework-agnostic UI abstractions
  - `MeshWeaver.AI` - Agent framework with plugin architecture
  - `MeshWeaver.Blazor` - Blazor Server implementation
  - `MeshWeaver.Data` - CRUD operations with activity tracking
  - `MeshWeaver.Documentation` - Embedded documentation (served under Doc/)
  - `MeshWeaver.Graph` - Graph node configuration and node type system

- **`samples/`** - Sample business domain applications
  - `Graph/Data/` - Sample data nodes (ACME, Northwind, Cornerstone, etc.)
  - `Graph/content/` - Static content files (icons, images, attachments)

- **`memex/`** - Memex Portal (recommended for development)
  - `Memex.Portal.Monolith/` - Development portal with full Graph support
  - `aspire/` - Microservices with .NET Aspire orchestration

### Architectural Patterns

**Request-Response**: Use `hub.AwaitResponse<TResponse>(request, o => o.WithTarget(address))` for operations requiring results.
The response is submitted as `hub.Post(responseMessage, o => o.ResponseFor(request))`.

**Fire-and-Forget**: Use `hub.Post(message, o => o.WithTarget(address))` for notifications and events.

**Address-Based Routing**: Services register at specific addresses (e.g., `bookings/q1_2025`, `app/northwind`, `pricing/id`).
Layout areas follow the pattern `@{address}/{areaName}/{areaId}`. The areaId is optional and depends on the view.
E.g. `{address}/Details/{itemId}` would render a details view for the item with `itemId`.

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
        hub.Post(new MyResponse(result), o => o.ResponseFor(request));
        return request.Processed();
    }
}
```

### AI Plugin Development
```csharp
public class MyPlugin(IMessageHub hub, IAgentChat chat)
{
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

- **.NET 10.0** - Target framework
- **Orleans** - Distributed deployment (distributed deployment, microservices)
- **Blazor Server** - Web UI framework
- **Microsoft.Extensions.AI** - AI integration
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
- Sample applications go in `samples/`
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
- Solution file: `MeshWeaver.slnx` contains 50+ projects
