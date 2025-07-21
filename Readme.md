# MeshWeaver

MeshWeaver is a modular framework for building data-driven applications with real-time updates, interactive visualizations, and powerful data processing capabilities.

## Getting Started

You can run MeshWeaver in two modes:

### Monolithic Setup
```bash
cd portal/MeshWeaver.Portal
dotnet run
```

This setup is useful for smaller projects which are deployed as monoliths. If you are unsure which approach to pick, pick this one.

### Microservices Setup (with .NET Aspire)
```bash
cd portal/aspire/MeshWeaver.Portal.AppHost
dotnet run
```

Please note that this approach requires running docker. Microservices are generally more complex to handle, but they provide big flexibility running in productive setups. 

## Creating New Projects

MeshWeaver provides project templates to quickly get started with new applications. These templates include a complete portal setup with Todo module examples, AI integration, and comprehensive documentation.

### Install the Template

First, install the MeshWeaver project templates:

```bash
dotnet new install MeshWeaver.ProjectTemplates
```

### Create a New Solution

Create a complete MeshWeaver solution with portal, Todo module, AI integration, and tests:

```bash
dotnet new meshweaver-solution -n MyApp
cd MyApp
dotnet run --project MyApp.Portal
```

This creates:
- **MyApp.Portal**: Main web portal with Blazor UI, AI chat, and article system
- **MyApp.Todo**: Todo module demonstrating data management and layout areas  
- **MyApp.Todo.AI**: AI agent for Todo management with natural language processing
- **MyApp.Todo.Test**: Unit tests for the Todo module

### Individual Project Templates

You can also create individual projects:

```bash
# Portal application only
dotnet new meshweaver-portal -n MyPortal

# Todo library only  
dotnet new meshweaver-todo -n MyTodo

# Todo AI library only
dotnet new meshweaver-todo-ai -n MyTodoAI

# Test project only
dotnet new meshweaver-test -n MyTests
```

### What's Included

The templates provide:

- **Complete Portal Infrastructure**: Authentication, navigation, responsive design
- **Reactive Data Management**: Real-time updates using MeshWeaver's messaging system
- **AI Integration**: Azure OpenAI integration with chat interface and AI agents
- **Layout Areas**: Dynamic UI areas with data binding and real-time updates
- **Documentation System**: Markdown-based articles with images and author information
- **Comprehensive Testing**: Unit test setup with FluentAssertions
- **Sample Application**: Full Todo application demonstrating CRUD operations

### Running Your Application

After creating a project:

1. Navigate to your portal project: `cd MyApp.Portal`
2. Run the application: `dotnet run`
3. Open your browser to: `https://localhost:5001`

The portal includes:
- **Todos**: Sample Todo application with real-time updates
- **Articles**: Complete documentation system with technical articles
- **Agents**: AI-powered chat interface for Todo management

### Template Versions

Templates are versioned alongside MeshWeaver releases. To update to the latest templates:

```bash
dotnet new uninstall MeshWeaver.ProjectTemplates
dotnet new install MeshWeaver.ProjectTemplates
``` 

## Core Components

### Message Hub System
The backbone of MeshWeaver is its message hub system (`MeshWeaver.Messaging.Hub`), which provides:
- Actor model-based message routing
- Request-response patterns
- Hierarchical hub hosting
- Built-in dependency injection

### Data Processing
- **Messaging**: Send and received messages between addresses. Route them inside the mesh.
- **Concurrency**: Fully asynchronous concurrency using the actor model.
- **Data Synchronization**: Full-fledged data replication for Create Read Update Delete.
- **Business Rules**: Rule engine with scope-based state management
- **Import**: Flexible data import system with activity tracking

### Computation
- **Kernel**: Interactive code execution and visualization
- **Interactive Markdown**: A markdown dialect allowing to include code execution.

### UI and Visualization
- **Layout**: Framework-agnostic UI control abstractions
- **Reporting**: Fexible and interactive reporting

### Flexible deployment options
- **Elasticity**: Create a fully elastic setup using Orleans
- **Integration**: Integrate with almost any available technology through Aspire. 

## Resources

- [Portal](https://portal.meshweaver.cloud) - Try out MeshWeaver and read our blog
- [Website](https://meshweaver.cloud) - Learn more about MeshWeaver
- [Discord](https://discord.gg/ACSYBWPy) - Join our community

## Architecture


### Deployment Options

1. **Monolithic** (`portal/MeshWeaver.Portal`)
   - Single process deployment
   - Simplified setup
   - Suitable for development and smaller deployments

2. **Microservices** (`portal/aspire/MeshWeaver.Portal.AppHost`)
   - .NET Aspire-based orchestration
   - Service discovery
   - Azure integration
   - PostgreSQL for persistence
   - Azure Blob Storage for articles

## Contributing

We welcome contributions! Join our [Discord](https://discord.gg/wMTug8qtvc) to discuss features, report issues, or get help. 