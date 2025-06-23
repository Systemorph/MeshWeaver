# MeshWeaver.Portal.AI

## Overview

MeshWeaver.Portal.AI provides AI-powered functionality specifically designed for the MeshWeaver Portal application. This library includes the default `MeshNavigator` agent and portal-specific AI service configurations.

## Features

- **MeshNavigator Agent**: Default AI assistant for portal navigation and user assistance
- **Portal Integration**: Seamless integration with MeshWeaver Portal infrastructure
- **Azure AI Foundry Support**: Built on top of MeshWeaver.AI.AzureFoundry for Azure OpenAI integration
- **Extensible Architecture**: Easy to add custom agents and extend functionality

## Installation

This package is part of the MeshWeaver Portal solution and should be referenced as a project dependency:

```xml
<ProjectReference Include="..\MeshWeaver.Portal.AI\MeshWeaver.Portal.AI.csproj" />
```

## Configuration

### 1. Service Registration

In your Portal application's service configuration:

```csharp
using MeshWeaver.Portal.AI;

// Add Portal AI services (includes MeshNavigator and Azure AI Foundry)
builder.Services.AddPortalAI();

// Configure AI credentials
builder.Services.Configure<AICredentialsConfiguration>(
    builder.Configuration.GetSection("AI"));
```

### 2. AI Configuration

Add to your `appsettings.json`:

```json
{
  "AI": {
    "Url": "https://your-azure-openai-endpoint.openai.azure.com/",
    "ApiKey": "your-api-key-here",
    "Models": ["gpt-4", "gpt-35-turbo"]
  }
}
```

## MeshNavigator Agent

The `MeshNavigator` is the default AI agent for the MeshWeaver Portal, marked with the `[DefaultAgent]` attribute.

### Capabilities

- **Portal Navigation**: Helps users navigate portal features and functionality
- **Feature Explanation**: Provides guidance on portal tools and components
- **General Assistance**: Offers support and answers user questions
- **Professional Interaction**: Maintains a friendly, helpful, and professional tone

### Agent Definition

```csharp
[DefaultAgent]
public class MeshNavigator : IAgentDefinition
{
    public string AgentName => "MeshNavigator";
    public string Description => "A helpful assistant for navigating the MeshWeaver portal...";
    public string Instructions => "You are MeshNavigator, a helpful AI assistant...";
}
```

## Usage

### In Blazor Components

The MeshNavigator agent is automatically available through the AI chat system:

```razor
@using MeshWeaver.Blazor.Chat
@inject IChatService ChatService

<Chat @ref="chatComponent"></Chat>

@code {
    private Chat chatComponent;
    
    protected override async Task OnInitializedAsync()
    {
        // The chat component will automatically use MeshNavigator as the default agent
        await base.OnInitializedAsync();
    }
}
```

### Programmatic Access

```csharp
public class PortalService
{
    private readonly IAgentChatFactory _chatFactory;

    public PortalService(IAgentChatFactory chatFactory)
    {
        _chatFactory = chatFactory;
    }

    public async Task<IAgentChat> StartNavigationChatAsync()
    {
        // Creates a chat with MeshNavigator as the default agent
        return await _chatFactory.CreateAsync();
    }
}
```

## Extending with Custom Agents

You can add custom agents alongside MeshNavigator:

```csharp
public class CustomPortalAgent : IAgentDefinition
{
    public string AgentName => "CustomAgent";
    public string Description => "Specialized agent for custom tasks";
    public string Instructions => "You are a specialized assistant for...";
}

// Register in services
builder.Services.AddSingleton<IAgentDefinition, CustomPortalAgent>();
```

## Architecture

### Service Registration Flow

```
AddPortalAI()
├── AddSingleton<IAgentDefinition, MeshNavigator>()
└── AddAIFoundry()
    └── AddSingleton<IAgentChatFactory, AzureAIChatCompletionAgentChatFactory>()
```

### Integration Points

- **MeshWeaver.AI.AzureFoundry**: Provides Azure OpenAI integration
- **MeshWeaver.Portal.Shared.Web**: Consumes Portal AI services
- **MeshWeaver.Blazor.Chat**: UI components for chat interaction

## Default Agent Selection

The `MeshNavigator` agent is marked with `[DefaultAgent]` and will be automatically selected as the primary agent for:

- Initial chat conversations
- Agent delegation scenarios
- Default routing when no specific agent is mentioned

## Customization

### Custom Instructions

You can customize the MeshNavigator's behavior by modifying its instructions:

```csharp
public class CustomMeshNavigator : IAgentDefinition
{
    public string AgentName => "MeshNavigator";
    public string Description => "Custom portal assistant";
    public string Instructions => "Your custom instructions here...";
}

// Replace the default registration
builder.Services.AddSingleton<IAgentDefinition, CustomMeshNavigator>();
```

### Additional Capabilities

Extend functionality by implementing specialized agent interfaces:

```csharp
public class EnhancedNavigator : IAgentDefinition, IAgentWithPlugins
{
    // Implement agent definition
    public string AgentName => "EnhancedNavigator";
    // ... other properties

    // Add plugin capabilities
    public IEnumerable<object> GetPlugins()
    {
        yield return new PortalNavigationPlugin();
        yield return new UserPreferencesPlugin();
    }
}
```

## Dependencies

- MeshWeaver.AI.AzureFoundry
- MeshWeaver.AI
- Microsoft.Extensions.DependencyInjection.Abstractions

## Related Projects

- [MeshWeaver.AI.AzureFoundry](../../src/MeshWeaver.AI.AzureFoundry/README.md) - Azure OpenAI integration
- [MeshWeaver.AI](../../src/MeshWeaver.AI/README.md) - Core AI services
- [MeshWeaver.Blazor.Chat](../../src/MeshWeaver.Blazor.Chat/README.md) - Chat UI components
- [MeshWeaver.Portal.Shared.Web](../MeshWeaver.Portal.Shared.Web/README.md) - Portal web infrastructure

## Contributing

This project is part of the MeshWeaver Portal ecosystem. Please follow the established patterns and conventions when contributing.

## License

This project is licensed under the MIT License - see the main MeshWeaver repository for details.
