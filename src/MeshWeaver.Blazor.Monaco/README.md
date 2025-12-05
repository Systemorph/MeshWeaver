# MeshWeaver.Blazor.Monaco

A Blazor component library that wraps the Monaco Editor (the editor powering VS Code) for use in MeshWeaver applications.

## Features

- Monaco Editor integration via [BlazorMonaco](https://github.com/nicknow/BlazorMonaco)
- Two-way data binding with `Value` and `ValueChanged`
- Light/dark theme support with automatic detection
- Custom completion providers (autocomplete) with:
  - Configurable trigger characters (e.g., `@` for mentions)
  - Synchronous mode with static completion items
  - Asynchronous mode with server-side fuzzy matching
- Placeholder text support
- Submit handling (Enter key triggers `OnSubmit`, Shift+Enter for newlines)
- Read-only mode support
- Fluent UI theme integration

## Installation

Add a reference to `MeshWeaver.Blazor.Monaco` in your project:

```xml
<ProjectReference Include="..\MeshWeaver.Blazor.Monaco\MeshWeaver.Blazor.Monaco.csproj" />
```

## Usage

### Basic Editor

```razor
@using MeshWeaver.Blazor.Monaco

<MonacoEditorView @bind-Value="content"
                  Placeholder="Type your message..."
                  Height="100px"
                  MaxHeight="300px"
                  OnSubmit="HandleSubmit" />

@code {
    private string content = "";

    private async Task HandleSubmit()
    {
        // Handle submission
    }
}
```

### With Static Completion Items

```razor
<MonacoEditorView @bind-Value="content"
                  CompletionProvider="completionConfig" />

@code {
    private CompletionProviderConfig completionConfig = new()
    {
        TriggerCharacters = ["@"],
        Items =
        [
            new CompletionItem
            {
                Label = "assistant",
                Description = "AI Assistant",
                Category = "Agents",
                Kind = CompletionItemKind.Module
            },
            new CompletionItem
            {
                Label = "help",
                Description = "Get help",
                Category = "Commands",
                Kind = CompletionItemKind.Function
            }
        ]
    };
}
```

### With Async Completion

```razor
<MonacoEditorView @bind-Value="content"
                  CompletionProvider="@(new CompletionProviderConfig { TriggerCharacters = ["@"] })"
                  AsyncCompletionCallback="GetCompletions" />

@code {
    private async Task<CompletionItem[]> GetCompletions(string query)
    {
        // Fetch completions from server based on query
        var results = await MyService.SearchAsync(query);
        return results.Select(r => new CompletionItem
        {
            Label = r.Name,
            Description = r.Description
        }).ToArray();
    }
}
```

## Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `Value` | `string?` | `null` | The editor content (supports two-way binding) |
| `ValueChanged` | `EventCallback<string>` | - | Callback when content changes |
| `OnSubmit` | `EventCallback` | - | Callback when Enter is pressed (without Shift) |
| `IsDisabled` | `bool` | `false` | Makes the editor read-only |
| `Placeholder` | `string` | `"Type your message..."` | Placeholder text when empty |
| `Height` | `string` | `"80px"` | Editor height |
| `MaxHeight` | `string` | `"200px"` | Maximum editor height |
| `CompletionProvider` | `CompletionProviderConfig?` | `null` | Static completion configuration |
| `AsyncCompletionCallback` | `Func<string, Task<CompletionItem[]>>?` | `null` | Async completion callback |

## CompletionItem Properties

| Property | Type | Description |
|----------|------|-------------|
| `Label` | `string` | Text shown in autocomplete dropdown (required) |
| `InsertText` | `string?` | Text to insert (defaults to Label) |
| `Description` | `string?` | Description shown in dropdown |
| `Detail` | `string?` | Additional detail text |
| `Category` | `string?` | Grouping category (e.g., "Agents", "Commands") |
| `Kind` | `CompletionItemKind` | Icon type (Text, Module, File, Function, Variable) |

## Public Methods

| Method | Description |
|--------|-------------|
| `GetValueAsync()` | Gets the current editor value |
| `SetValueAsync(string)` | Sets the editor value |
| `ClearAsync()` | Clears the editor content |
| `FocusAsync()` | Focuses the editor |
| `SetReadOnlyAsync(bool)` | Sets the read-only state |

## CodeEditorControl

For server-side layout definitions, use `CodeEditorControl`:

```csharp
var editor = new CodeEditorControl()
    .WithValue("initial content")
    .WithLanguage("csharp")
    .WithTheme("vs-dark")
    .WithHeight("300px")
    .WithLineNumbers(true)
    .WithMinimap(false)
    .WithWordWrap(true)
    .WithPlaceholder("Enter code here...");
```

## Dependencies

- [BlazorMonaco](https://github.com/nicknow/BlazorMonaco) - Monaco Editor wrapper for Blazor
- [Microsoft.FluentUI.AspNetCore.Components](https://github.com/microsoft/fluentui-blazor) - Fluent UI components
- MeshWeaver.Layout - MeshWeaver layout system
