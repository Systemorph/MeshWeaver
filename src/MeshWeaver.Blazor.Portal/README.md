# MeshWeaver.Blazor.Portal

Provides the Blazor Server portal layout infrastructure including the main application shell, navigation, side panel, chat integration, authentication, and responsive viewport management.

## Features

- `PortalLayoutBase` -- base layout component with desktop/mobile navigation, dynamic menu items, and splitter-based side panel
- Side panel system with persistent state, position control, and thread chat integration
- Authentication navigation services and configurable auth options
- Responsive viewport management via `DimensionManager` and `BrowserDimensionWatcher`
- Search bar, user profile components, site settings panel, and app version service
- Chat side panel with thread support and `@` node autocomplete

## Usage

```csharp
// On the Blazor server-side builder
builder.AddBlazorPortalServices();

// Or register core services only (without side panel persistence)
services.AddBlazorPortalCoreServices();
```

Inherit from `PortalLayoutBase` in your layout Razor component to get the full portal shell with navigation, side panel, and menu integration.

## Dependencies

- `MeshWeaver.Blazor` -- base Blazor component infrastructure
- `MeshWeaver.AI` / `MeshWeaver.AI.Application` -- chat and thread support
- `MeshWeaver.Graph` -- graph node types and navigation
- `Microsoft.FluentUI.AspNetCore.Components` -- Fluent UI component library
