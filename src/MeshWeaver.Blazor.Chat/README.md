# MeshWeaver.Blazor.Chat

## Overview
MeshWeaver.Blazor.Chat provides Blazor UI components for creating interactive AI-powered chat interfaces within MeshWeaver applications. This library offers a simple yet powerful chat experience that integrates with MeshWeaver.AI services to enable intelligent conversations in your applications.

## Features
- **Simple Chat Interface**: Ready-to-use chat component for AI interactions
- **Message Visualization**: Support for text, markdown, and other content types
- **Real-time Updates**: Live chat interface with immediate message rendering
- **Seamless AI Integration**: Built to work with MeshWeaver.AI services
- **Customizable Layout**: Flexible positioning with overlay and resizing capabilities
- **Chat History**: Support for maintaining conversation history
- **Responsive Design**: Works on both desktop and mobile interfaces

## Usage

### Basic Implementation
To use the Chat component in your Blazor application, simply add the namespace and include the component in your layout or page:

```cshtml
@using MeshWeaver.Blazor.Chat

<!-- Simple usage -->
<Chat @ref="chatComponent"></Chat>
```

### Integration in Layout (as shown in MeshWeaver Portal)
```cshtml
@using MeshWeaver.Blazor.Chat

<!-- Button to toggle chat visibility -->
<div class="chat">
    <FluentButton BackgroundColor="transparent" OnClick="ToggleAIChatVisibility" Title="AI Chat">
        <FluentIcon Value="@(new Icons.Regular.Size20.Chat())" Color="Color.Neutral" />
    </FluentButton>
</div>

<!-- Chat overlay with custom container and resizing -->
@if (IsAIChatVisible)
{
    <div class="ai-chat-overlay">
        <div class="ai-chat-container">
            <div class="ai-chat-resizer" @onmousedown="@(e => StartResize(e))"></div>
            <div class="ai-chat-content">
                <div class="ai-chat-header">
                    <span>AI Chat</span>
                    <div class="ai-chat-header-controls">
                        <FluentButton Appearance="Appearance.Stealth" OnClick="HandleNewChatAsync" Title="New chat">
                            <FluentIcon Value="@(new Icons.Regular.Size16.Add())" />
                        </FluentButton>
                        <FluentButton Appearance="Appearance.Stealth" OnClick="ToggleAIChatVisibility" Title="Close">
                            <FluentIcon Value="@(new Icons.Regular.Size20.Dismiss())" />
                        </FluentButton>
                    </div>
                </div>
                <Chat @ref="chatComponent"></Chat>
            </div>
        </div>
    </div>
}
```

### Required Code-behind Methods
The following methods should be implemented in your code-behind file:

```csharp
private bool IsAIChatVisible { get; set; } = false;
private Chat chatComponent;

private void ToggleAIChatVisibility()
{
    IsAIChatVisible = !IsAIChatVisible;
    StateHasChanged();
}

private async Task HandleNewChatAsync()
{
    // Start a new chat session
    if (chatComponent != null)
    {
        await chatComponent.StartNewChatAsync();
    }
}

// For resizable chat panel
private void StartResize(MouseEventArgs e)
{
    // Implement resize handling
}
```

## Integration with MeshWeaver
- Uses MeshWeaver.AI services for intelligent chat capabilities
- Built on top of MeshWeaver.Blazor components for UI consistency
- Integrates with MeshWeaver design system and Fluent UI
- Works within MeshWeaver's messaging architecture for real-time updates
- Can be deployed in both monolithic and microservice setups

## Components
- **Chat**: The main component that provides the complete chat experience
- **ChatMessage**: Representation of individual chat messages
- **ChatInput**: User input field with submission handling
- **ChatDisplay**: Visual rendering of conversation history

## Related Projects
- [MeshWeaver.AI](../MeshWeaver.AI/README.md) - Core AI services integration
- [MeshWeaver.Blazor](../MeshWeaver.Blazor/README.md) - Blazor component library
- [MeshWeaver.Messaging.Hub](../MeshWeaver.Messaging.Hub/README.md) - Message routing system

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall architecture.
