# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Development Commands

### Build and Test
```bash
# Build entire solution
dotnet build

# Run tests (uses xUnit v3)
dotnet test

# Run specific test project
dotnet test test/MeshWeaver.Test.csproj

# Clean solution
dotnet clean
```

### Running Applications

#### Monolithic Portal (Development)
```bash
cd portal/MeshWeaver.Portal
dotnet run
# Access at https://localhost:7079
```

#### Microservices Portal (.NET Aspire)
```bash
cd portal/aspire/MeshWeaver.Portal.AppHost
dotnet run
# Access Aspire dashboard for service management
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

**Request-Response**: Use `hub.SendRequest<TResponse>(request)` for operations requiring results. 

**Fire-and-Forget**: Use `hub.Send(message)` for notifications and events.

**Address-Based Routing**: Services register at specific addresses (e.g., `@app/data`, `@app/northwind/dashboard`). Layout areas follow the pattern `@app/{address}/{areaName}`.

**Reactive UI**: All UI state changes flow through the message hub. Controls are immutable records that specify their current state.

## Development Patterns

### Adding New Layout Areas
```csharp
public record MyLayoutArea(string Title) : LayoutAreaBase<MyLayoutArea>
{
    public override RenderFragment Render() => @<div>@Title</div>;
}
```

### Message Handling
```csharp
[RequestHandler]
public async Task<MyResponse> HandleRequest(MyRequest request)
{
    // Process request
    return new MyResponse();
}
```

### AI Plugin Development
```csharp
public class MyPlugin(IMessageHub hub)
{
    [KernelFunction]
    public async Task<string> DoSomething(string input)
    {
        var response = await hub.SendRequest<MyResponse>(new MyRequest(input));
        return response.Result;
    }
}
```

## Key Dependencies

- **.NET 9.0** - Target framework
- **Orleans** - Distributed deployment (microservices)
- **Blazor Server** - Web UI framework  
- **Semantic Kernel** - AI integration
- **xUnit v3** - Testing framework
- **FluentAssertions** - Test assertions
- **Chart.js** - Data visualization
- **Azure SDKs** - Cloud integration

## Testing Guidelines

Tests use xUnit v3 with structured logging. Use `MeshWeaver.Fixture` for test infrastructure:

```csharp
public class MyTest : IAsyncLifetime
{
    private MessageHub hub;
    
    public async Task InitializeAsync()
    {
        hub = new MessageHubConfiguration().WithSomeService().CreateHub();
    }
}
```

## Project Structure Guidelines

- Framework code belongs in `src/`
- Business modules go in `modules/` 
- Each module should have its own address space (e.g., `@app/northwind`)
- UI components should be framework-agnostic in the layout layer
- AI agents should use plugins to access application functionality

## Solution Management

The solution uses centralized package management via `Directory.Packages.props`. When adding new dependencies, update the central package file rather than individual project files.

Current version: 2.3.0 (managed centrally in `Directory.Build.props`)