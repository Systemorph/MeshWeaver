# MeshWeaver.Blazor.Chat

A comprehensive Blazor chat component library for building interactive chat interfaces with AI integration support. This library provides a complete set of reusable chat UI components designed to work seamlessly with Microsoft.Extensions.AI and MeshWeaver's messaging system.

## Features

- 🎨 **Modern UI Components** - Beautiful, responsive chat interface components
- 🤖 **AI Agent Integration** - Built-in support for AI agents and function calling
- 📝 **Markdown Support** - Rich text rendering with markdown support
- 🔍 **Citation Support** - Display and link to document citations
- 📱 **Responsive Design** - Mobile-friendly chat interface
- ⚡ **Real-time Updates** - Live message updates and progress indicators
- 📚 **Chat History** - Persistent conversation management
- 🎯 **Auto-scroll** - Automatic scrolling to latest messages

## Components

### Core Chat Components

#### `ChatMessageList`
Displays a list of chat messages with automatic scrolling and progress indicators.
```razor
<ChatMessageList Messages="@messages" 
                 InProgressMessage="@currentMessage" 
                 NoMessagesContent="@emptyStateContent" />
```

#### `ChatMessageItem`
Renders individual chat messages with role-based styling and content formatting.
- Supports user messages and AI assistant responses
- Displays function calls and their execution status
- Renders citations and references
- Markdown content support

#### `ChatInput`
Interactive input component for sending messages with agent selection.
```razor
<ChatInput OnSend="@HandleMessageSent" 
           IsDisabled="@isProcessing" 
           Agents="@availableAgents" />
```

#### `ChatHeader`
Header component with new chat functionality.
```razor
<ChatHeader OnNewChat="@StartNewConversation" />
```

#### `ChatHistorySelector`
Sidebar component for managing conversation history.
```razor
<ChatHistorySelector SelectedConversationId="@currentConversationId"
                     OnConversationSelected="@LoadConversation" />
```

#### `ChatCitation`
Component for displaying document citations and references.
```razor
<ChatCitation File="@citation.File" 
              PageNumber="@citation.Page" 
              Quote="@citation.Quote" />
```

## Installation

Add the package reference to your project:

```xml
<PackageReference Include="MeshWeaver.Blazor.Chat" Version="2.2.0" />
```

## Dependencies

This library depends on:
- `Microsoft.AspNetCore.Components.Web`
- `MeshWeaver.AI` - AI integration and agent management
- `MeshWeaver.Blazor` - Base Blazor components and utilities
- `MeshWeaver.Layout` - Layout and UI abstractions
- `MeshWeaver.Messaging.Hub` - Message routing and communication

## Usage

### Basic Chat Setup

1. **Add the component to your Blazor page:**

```razor
@page "/chat"
@using MeshWeaver.Blazor.Chat
@using Microsoft.Extensions.AI

<div class="chat-container">
    <ChatHeader OnNewChat="@StartNewChat" />
    
    <ChatMessageList Messages="@Messages" 
                     InProgressMessage="@InProgressMessage" />
    
    <ChatInput OnSend="@SendMessage" 
               IsDisabled="@IsProcessing" 
               Agents="@AvailableAgents" />
</div>

@code {
    private List<ChatMessage> Messages = new();
    private ChatMessage? InProgressMessage;
    private bool IsProcessing;
    private List<IAgentDefinition> AvailableAgents = new();
    
    private async Task SendMessage(ChatMessage message)
    {
        Messages.Add(message);
        IsProcessing = true;
        
        // Process message with your AI service
        var response = await ProcessMessageAsync(message);
        
        Messages.Add(response);
        IsProcessing = false;
        StateHasChanged();
    }
    
    private void StartNewChat()
    {
        Messages.Clear();
        InProgressMessage = null;
    }
}
```

2. **Include the required CSS and JavaScript:**

The components automatically load their required assets, including:
- `ChatMessageList.razor.js` - Auto-scrolling behavior
- `ChatInput.razor.js` - Input handling and agent selection
- Component-specific CSS files for styling

### Advanced Features

#### Agent Selection
The chat input supports agent selection with dropdown functionality:

```razor
<ChatInput OnSend="@SendMessage"
           Agents="@GetAvailableAgents()"
           OnAgentSelected="@HandleAgentSelection" />
```

#### Function Call Display
The message components automatically render function calls and their progress:

```csharp
// Function calls are displayed with progress indicators
// and status updates automatically
```

#### Citation Support
Citations are automatically extracted and displayed:

```razor
<!-- Citations appear inline with assistant messages -->
<ChatCitation File="document.pdf" PageNumber="5" Quote="Relevant quote text" />
```

## Styling

The components come with built-in CSS classes that can be customized:

- `.message-list-container` - Main message container
- `.user-message` - User message styling
- `.assistant-message` - AI assistant message styling
- `.assistant-function` - Function call display
- `.input-box` - Chat input container
- `.chat-header-container` - Header styling

You can override these styles in your application's CSS to match your design system.

## Integration with MeshWeaver

This library is designed to work seamlessly with the broader MeshWeaver ecosystem:

- **Message Hub**: Integrates with `MeshWeaver.Messaging.Hub` for real-time communication
- **AI Services**: Works with `MeshWeaver.AI` for agent management and function calling
- **Layout System**: Uses `MeshWeaver.Layout` for consistent UI patterns

## Examples

Check out the MeshWeaver documentation and sample applications for complete implementation examples.

## Contributing

This library is part of the MeshWeaver project. Please refer to the main project repository for contribution guidelines.

## License

This project is licensed under the same terms as the main MeshWeaver project.