# MeshWeaver.AI

## Overview
MeshWeaver.AI provides AI integration capabilities for the MeshWeaver framework, enabling AI-powered features and services within your applications. This library connects MeshWeaver applications with Azure OpenAI and other AI services for natural language processing, content generation, and intelligent assistance.

## Features
- **AI Service Integration**: Connect to Azure OpenAI and other AI services
- **Chat Functionality**: Built-in support for chat completions and conversations
- **Function Calling**: Execute functions directly from AI models
- **Progress Tracking**: Real-time AI operation progress monitoring
- **Credential Management**: Secure management of AI service credentials
- **Custom Model Support**: Configure and use different AI models based on requirements

## Usage

### Basic Configuration
```csharp
// In Program.cs or Startup.cs
using MeshWeaver.AI;

var builder = WebApplication.CreateBuilder(args);

// Add AI services to the DI container
builder.Services.AddAI(config => config
    .WithSystemPrompt("You are a helpful assistant.")
    .WithModels("gpt-4.1-mini", "o3-mini"));

// Configure AI credentials
builder.Services.Configure<AICredentialsConfiguration>(builder.Configuration.GetSection("AI"));
```

### Using the Chat Service
```csharp
public class MyService
{
    private readonly IChatService _chatService;

    public MyService(IChatService chatService)
    {
        _chatService = chatService;
    }

    public async Task<string> GetAIResponse(string userMessage)
    {
        var client = _chatService.Get();
        var response = await client.CompleteChat(userMessage);
        return response;
    }
}
```

## Configuration
The AI services require proper configuration in your application's settings:

```json
{
  "AI": {
    "Url": "https://your-azure-openai-endpoint.com",
    "ApiKey": "your-api-key-here",
    "Models": ["o3-mini"]
  }
}
```

## Integration with MeshWeaver
- Works with MeshWeaver.Messaging.Hub for message-based AI operations
- Integrates with MeshWeaver.Blazor.Chat for UI components
- Compatible with both monolithic and Orleans hosting
- Supports real-time updates through MeshWeaver layout system

## Related Projects
- [MeshWeaver.Blazor.Chat](../MeshWeaver.Blazor.Chat/README.md) - Blazor UI components for chat interfaces
- [MeshWeaver.Messaging.Hub](../MeshWeaver.Messaging.Hub/README.md) - Core messaging for AI communications

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall architecture.
