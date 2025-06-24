# MeshWeaver.AI.AzureFoundry

## Overview

MeshWeaver.AI.AzureFoundry provides Azure AI Foundry integration for the MeshWeaver AI framework. Azure AI Foundry is a comprehensive platform for building, deploying, and managing AI applications at scale, featuring the Azure AI Agent Service for orchestrating and hosting AI agents.

**Note**: This implementation is currently a placeholder as full Azure AI Foundry Agent Service integration is still being developed. For production scenarios with Azure OpenAI, consider using [MeshWeaver.AI.AzureOpenAI](../MeshWeaver.AI.AzureOpenAI/README.md).

## Features

- **Azure AI Foundry Integration**: Framework for connecting to Azure AI Foundry project endpoints
- **Agent Service Ready**: Prepared for Azure AI Foundry Agent Service when fully available
- **Configuration-Based Setup**: Uses `AzureAIFoundryConfiguration` for project setup
- **Managed Identity Support**: Built-in support for Azure managed identity authentication
- **Extensible Architecture**: Built on top of MeshWeaver.AI's `ChatCompletionAgentChatFactory` base class

## Azure AI Foundry Capabilities

Azure AI Foundry provides:

- **Model Catalog**: Access to various AI models from different providers
- **Agent Service**: Fully managed service for building, deploying, and scaling AI agents
- **Project Management**: Unified project endpoints for accessing models and services
- **Security & Governance**: Built-in responsible AI practices and monitoring
- **Tool Integration**: Built-in tools like Azure AI Search, Bing Search, Code Interpreter, and more

## Installation

This package is part of the MeshWeaver solution and should be referenced as a project dependency:

```xml
<ProjectReference Include="..\MeshWeaver.AI.AzureFoundry\MeshWeaver.AI.AzureFoundry.csproj" />
```

## Configuration

### 1. Configure Azure AI Foundry

Add the following configuration to your `appsettings.json`:

```json
{
  "AzureAIFoundry": {
    "ProjectEndpoint": "https://your-project.cognitiveservices.azure.com/",
    "DefaultModelName": "gpt-4",
    "Models": ["gpt-4", "gpt-35-turbo"],
    "UseManagedIdentity": true,
    "SubscriptionId": "your-subscription-id",
    "ResourceGroupName": "your-resource-group",
    "ProjectName": "your-project-name"
  }
}
```

### 2. Register Services

In your `Program.cs` or service configuration:

```csharp
using MeshWeaver.AI.AzureFoundry;

// Configure Azure AI Foundry
builder.Services.Configure<AzureAIFoundryConfiguration>(
    builder.Configuration.GetSection("AzureAIFoundry"));

// Add Azure AI Foundry services
builder.Services.AddAzureAIFoundry();
```

## Usage

### Basic Implementation

```csharp
public class MyService
{
    private readonly IAgentChatFactory _chatFactory;

    public MyService(IAgentChatFactory chatFactory)
    {
        _chatFactory = chatFactory;
    }

    public async Task<IAgentChat> CreateChatAsync()
    {
        var agentChat = await _chatFactory.CreateAsync();
        return agentChat;
    }
}
```

### Agent Definition

Create custom agents by implementing `IAgentDefinition`:

```csharp
public class MyCustomAgent : IAgentDefinition
{
    public string Name => "MyAgent";
    public string Description => "A custom AI agent for specific tasks";
    public string Instructions => "You are a helpful assistant specialized in...";
}
```

## Current Status

This implementation is currently a **placeholder** while Azure AI Foundry Agent Service integration is being developed. The current version:

- ✅ Provides configuration structure for Azure AI Foundry
- ✅ Supports authentication via managed identity or DefaultAzureCredential
- ⏳ **Placeholder**: Full Azure AI Foundry Agent Service integration
- ⏳ **Placeholder**: Model inference through Azure AI Foundry endpoints
- ⏳ **Placeholder**: Integration with Azure AI Foundry tools and capabilities

For immediate production needs with Azure OpenAI, use [MeshWeaver.AI.AzureOpenAI](../MeshWeaver.AI.AzureOpenAI/README.md).

## Future Roadmap

When Azure AI Foundry Agent Service becomes fully available, this package will support:

- **Native Agent Service**: Direct integration with Azure AI Foundry Agent Service
- **Tool Integration**: Built-in support for Azure AI Search, Bing Search, Code Interpreter
- **Model Catalog**: Access to the full Azure AI Foundry model catalog
- **Evaluation & Monitoring**: Built-in evaluation and monitoring capabilities
- **RAG Solutions**: Seamless integration with Azure AI Search for RAG scenarios

## Architecture

### Class Hierarchy

```
ChatCompletionAgentChatFactory (MeshWeaver.AI)
└── AzureAIFoundryChatCompletionAgentChatFactory (MeshWeaver.AI.AzureFoundry)
```

### Key Components

- **AzureAIFoundryChatCompletionAgentChatFactory**: Main factory class (currently placeholder)
- **AzureFoundryExtensions**: Extension methods for service registration
- **AzureAIFoundryConfiguration**: Configuration model for Azure AI Foundry projects

## Authentication

Azure AI Foundry supports multiple authentication methods:

- **Managed Identity** (recommended for production): Automatically authenticates using the assigned managed identity
- **DefaultAzureCredential**: Supports multiple authentication methods including:
  - Azure CLI
  - Visual Studio
  - Environment variables
  - Interactive browser authentication

## Dependencies

- Azure.AI.Inference (when available)
- Azure.Identity
- Azure.Core
- Microsoft.SemanticKernel
- Microsoft.Extensions.Options
- Microsoft.Extensions.DependencyInjection.Abstractions
- Microsoft.Extensions.Logging.Abstractions
- MeshWeaver.AI
- MeshWeaver.Messaging.Hub

## Related Projects

- [MeshWeaver.AI](../MeshWeaver.AI/README.md) - Core AI services and abstractions
- [MeshWeaver.AI.AzureOpenAI](../MeshWeaver.AI.AzureOpenAI/README.md) - Azure OpenAI integration (production ready)
- [MeshWeaver.Portal.AI](../../portal/MeshWeaver.Portal.AI/README.md) - Portal-specific AI implementations
- [MeshWeaver.Blazor.Chat](../MeshWeaver.Blazor.Chat/README.md) - Chat UI components

## Contributing

This project is part of the MeshWeaver ecosystem. Please follow the established patterns and conventions when contributing.

As Azure AI Foundry Agent Service becomes more mature, contributions to enhance this integration are welcome.

## License

This project is licensed under the MIT License - see the main MeshWeaver repository for details.
