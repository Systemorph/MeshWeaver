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