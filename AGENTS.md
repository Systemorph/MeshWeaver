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

## Bash Command Guidelines

**Always chain `cd` with the following command using `&&`** so the user only has to approve once:
```bash
# CORRECT — single approval
cd /c/dev/MeshWeaver && dotnet build

# WRONG — requires two separate approvals
cd /c/dev/MeshWeaver
dotnet build
```

When running build or test commands, prefer absolute paths or `&&`-chained commands to avoid multiple prompts.

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

## Data Access Patterns

**IMPORTANT:** Application code must never use `IPersistenceService` or `IMeshCatalog` directly — these are internal infrastructure interfaces.

### Reads — Use IMeshQuery
```csharp
var query = hub.ServiceProvider.GetRequiredService<IMeshQuery>();
var node = await query.QueryAsync("path:org/Acme", maxResults: 1).FirstOrDefaultAsync(ct);
```

### Creates/Deletes — Use IMeshNodeFactory
```csharp
var factory = hub.ServiceProvider.GetRequiredService<IMeshNodeFactory>();
await factory.CreateNodeAsync(node, createdBy: userId, ct);
await factory.DeleteNodeAsync(path, recursive: true, ct);
```

### Updates/Moves — Use message requests
```csharp
hub.Post(new UpdateNodeRequest(updatedNode));
await hub.AwaitResponse(new MoveNodeRequest(sourcePath, targetPath), ct);
hub.Post(new DataChangeRequest { Updates = [entity] });
```

### Service Resolution
Always use `GetRequiredService<T>()` for core services (`IMeshNodeFactory`, `IMeshQuery`). Never use `GetService<T>()` + null check for services that must be registered.

For full documentation see `src/MeshWeaver.Documentation/Data/Architecture/DataAccessPatterns.md`.

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
- `methodTimeout: 60000ms` (1 minute per test method)

**No mocking.** Tests that need infrastructure (persistence, messaging, DI) must use `MonolithMeshTestBase` or `OrleansTestBase` — never mock `IMessageHub`, `IMeshQuery`, or other core interfaces.

### Running Tests — Log Once, Read on Failure

**Never run the same test suite repeatedly** just to see results. Run once, capture output, and analyze failures from the log file.

**CRITICAL: Always use `run_in_background: true`** for test runs. Tests can take minutes — never block the conversation waiting for them. Use `timeout: 180000` (3 min) max for Bash test commands. The xunit.runner.json `methodTimeout` is 60000ms (1 min) per test method.

```bash
# Run tests in background, capture output — ALWAYS use run_in_background
cd /c/dev/MeshWeaver && dotnet test test/MeshWeaver.Hosting.Monolith.Test --no-restore 2>&1 | tee /tmp/monolith-test-results.log

# On failure: read the log file for error details (DO NOT re-run)
cat /tmp/monolith-test-results.log | grep -A 5 "FAIL"
```

**Workflow:**
1. Run tests **once** in background with output captured to a file
2. If failures: read the log file to understand errors
3. Fix the code
4. Run tests **once** again to verify fixes
5. Repeat 2–4 until green

### DevLogin and Access Control in Tests

`MonolithMeshTestBase` automatically logs in `rbuergi@systemorph.com` as Admin via `TestUsers.DevLogin(Mesh)` in `InitializeAsync()`. This means all tests start with a logged-in admin user — no manual setup needed for basic CRUD.

**TestUsers** (`MeshWeaver.Hosting.Monolith.TestBase.TestUsers`):
- `TestUsers.Admin` — default admin AccessContext
- `TestUsers.SampleUsers()` — MeshNode array of sample users from `samples/Graph/Data/User/`
- `TestUsers.DevLogin(mesh)` — logs in the admin user (called automatically by base class)
- `builder.AddSampleUsers()` — extension to pre-seed user MeshNodes in `ConfigureMesh`

When tests with `AddRowLevelSecurity()` need **per-user** access control (e.g., testing that User1 can't see User2's data), use explicit admin setup for data creation:

```csharp
// Before creating test data: set up admin context
var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
await securityService.AddUserRoleAsync("setup-admin", "Admin", null, "system");
accessService.SetCircuitContext(new AccessContext { ObjectId = "setup-admin", Name = "Setup Admin" });

// ... create test nodes ...

// After setup: clear admin context so tests start clean
accessService.SetCircuitContext(null);
```

### Node Types

Only use **registered** node types in tests. Standard types registered by `AddGraph()`:
`Markdown`, `Code`, `Agent`, `Group`, `User`, `VUser`, `Role`, `Notification`, `Approval`, `AccessAssignment`, `GroupMembership`, `PartitionAccessPolicy`, `ActivityLog`, `UserActivity`, `Comment`, `Thread`, `ThreadMessage`

Custom types can be registered via `builder.AddMeshNodes(new MeshNode("MyType") { Name = "My Type" })` in `ConfigureMesh`.

### MonolithMeshTestBase (recommended for most tests)

Reference `MeshWeaver.Hosting.Monolith.TestBase` and inherit from `MonolithMeshTestBase`:

```csharp
public class MyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Override ConfigureMesh to add services and sample users
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddSampleUsers()
            .ConfigureHub(hub => hub.AddMyHub());

    [Fact]
    public async Task MyTestMethod()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var nodeFactory = Mesh.ServiceProvider.GetRequiredService<IMeshNodeFactory>();

        // Create test data
        await nodeFactory.CreateNodeAsync(new MeshNode("test", "Namespace") { Name = "Test" }, "testuser");

        // Query
        var result = await meshQuery.QueryAsync<MeshNode>("path:Namespace/test scope:exact").FirstOrDefaultAsync();
        result.Should().NotBeNull();
    }
}
```

### HubTestBase (for message routing / layout tests)

```csharp
public class MyTest : HubTestBase, IAsyncLifetime
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration config)
        => base.ConfigureHost(config).AddNorthwindHub();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration config)
        => base.ConfigureClient(config).AddLayoutClient();

    [Fact]
    public async Task MyTestMethod()
    {
        var hub = GetClient();
        var response = await hub.AwaitResponse<MyResponse>(request, o => o.WithTarget(new HostAddress()));
        response.Should().NotBeNull();
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
