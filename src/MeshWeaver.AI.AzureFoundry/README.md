# MeshWeaver.AI.AzureFoundry

## Overview

MeshWeaver.AI.AzureFoundry provides Azure OpenAI integration for the MeshWeaver AI framework, enabling AI-powered agent chats using Azure OpenAI's ChatCompletionAgent. This library is designed for stateless chat completion scenarios without persistent assistant storage.

## Features

- **Azure OpenAI Integration**: Direct integration with Azure OpenAI services
- **ChatCompletionAgent Support**: Uses Microsoft Semantic Kernel's ChatCompletionAgent for stateless operations
- **Factory Pattern**: Implements the factory pattern for creating and managing agent chats
- **Configuration-Based Setup**: Uses `AICredentialsConfiguration` for secure credential management
- **Extensible Architecture**: Built on top of MeshWeaver.AI's `ChatCompletionAgentChatFactory` base class

## Installation

This package is part of the MeshWeaver solution and should be referenced as a project dependency:

```xml
<ProjectReference Include="..\MeshWeaver.AI.AzureFoundry\MeshWeaver.AI.AzureFoundry.csproj" />
```

## Configuration

### 1. Configure AI Credentials

Add the following configuration to your `appsettings.json`:

```json
{
  "AI": {
    "Url": "https://your-azure-openai-endpoint.openai.azure.com/",
    "ApiKey": "your-api-key-here",
    "Models": ["gpt-4", "gpt-35-turbo"]
  }
}
```

### 2. Register Services

In your `Program.cs` or service configuration:

```csharp
using MeshWeaver.AI.AzureFoundry;

// Configure AI credentials
builder.Services.Configure<AICredentialsConfiguration>(
    builder.Configuration.GetSection("AI"));

// Add Azure AI Foundry services
builder.Services.AddAIFoundry();
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
    public string AgentName => "MyAgent";
    public string Description => "A custom AI agent for specific tasks";
    public string Instructions => "You are a helpful assistant specialized in...";
}
```

## Architecture

### Class Hierarchy

```
ChatCompletionAgentChatFactory (MeshWeaver.AI)
└── AzureAIChatCompletionAgentChatFactory (MeshWeaver.AI.AzureFoundry)
```

### Key Components

- **AzureAIChatCompletionAgentChatFactory**: Main factory class for creating Azure OpenAI-powered agent chats
- **AzureFoundryExtensions**: Extension methods for service registration
- **AICredentialsConfiguration**: Configuration model for Azure OpenAI credentials

## Security Considerations

- **API Key Management**: Store API keys securely using Azure Key Vault or similar secure storage
- **Environment Variables**: Consider using environment variables for sensitive configuration
- **Network Security**: Ensure secure communication with Azure OpenAI endpoints

## Dependencies

- Microsoft.SemanticKernel
- Microsoft.Extensions.Options
- Microsoft.Extensions.DependencyInjection.Abstractions
- MeshWeaver.AI
- MeshWeaver.Messaging.Hub

## Troubleshooting

### Common Issues

1. **Missing API Key**: Ensure `AICredentialsConfiguration.ApiKey` is properly configured
2. **Invalid Endpoint**: Verify the `AICredentialsConfiguration.Url` format
3. **Model Not Available**: Check that the specified models exist in your Azure OpenAI deployment

### Logging

Enable detailed logging to troubleshoot issues:

```csharp
builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Debug);
});
```

## Related Projects

- [MeshWeaver.AI](../MeshWeaver.AI/README.md) - Core AI services and abstractions
- [MeshWeaver.Portal.AI](../../portal/MeshWeaver.Portal.AI/README.md) - Portal-specific AI implementations
- [MeshWeaver.Blazor.Chat](../MeshWeaver.Blazor.Chat/README.md) - Chat UI components

## Contributing

This project is part of the MeshWeaver ecosystem. Please follow the established patterns and conventions when contributing.

## License

This project is licensed under the MIT License - see the main MeshWeaver repository for details.
