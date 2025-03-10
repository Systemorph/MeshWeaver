# MeshWeaver.Layout

## Overview
MeshWeaver.Layout provides core abstractions for UI controls in the MeshWeaver ecosystem. It defines view models that can be implemented by different UI frameworks (like Blazor) and supports data binding, templating, and interactive updates.

## Installation

### Server Configuration
```csharp
// Configure layout in message hub
configuration
    .AddLayout(layout =>
        layout
            .WithView(
                "StaticView",
                Controls.Stack.WithView("Hello", "Hello").WithView("World", "World")
            )
            .WithView("ViewWithProgress", ViewWithProgress)
            .WithView("UpdatingView", UpdatingView())
            .WithView(
                "ItemTemplate",
                layout.Hub.GetWorkspace()
                    .GetStream(typeof(DataRecord))
                    .Select(x => x.Value.GetData<DataRecord>())
                    .DistinctUntilChanged()
                    .BindMany(record => Controls.Text(record.DisplayName))
            )
    );
```

### Client Configuration
```csharp
// Configure layout client
configuration.AddLayoutClient();
```

## Usage Examples

### Basic Controls
```csharp
// Stack of HTML controls
Controls.Stack
    .WithView("Hello", "Hello")
    .WithView("World", "World")
```

### Data Binding
```csharp
// Binding to a data model
record Toolbar(int Year);
var toolbar = new Toolbar(2024);

return Controls.Stack
    .WithView(
        Template.Bind(
            toolbar, 
            tb => Controls.Text(tb.Year), 
            "toolbar"
        ), 
        "Toolbar"
    )
    .WithView((area, _) =>
        area.GetDataStream<Toolbar>("toolbar")
            .Select(tb => Controls.Html($"Report for year {tb.Year}")), 
        "Content"
    );
```

### Interactive Controls
```csharp
// Counter with click action
Controls.Stack
    .WithView(
        Controls.Html("Increase Counter")
            .WithClickAction(context =>
                context.Host.UpdateArea(
                    new("Counter/Counter"),
                    Controls.Html((++counter))
                )
            ), 
        "Button"
    )
    .WithView(
        Controls.Html(counter.ToString()), 
        "Counter"
    );
```

### Data Grid
```csharp
// Data grid with columns
var data = new DataRecord[] { new("1", "1"), new("2", "2") };
return area.ToDataGrid(data, grid => grid
    .WithColumn(x => x.SystemName)
    .WithColumn(x => x.DisplayName)
);
```

### Item Templates
```csharp
// Template for repeating items
data.BindMany(record => 
    Controls.Text(record.DisplayName)
        .WithId(record.SystemName)
);
```

### Progress Indicators
```csharp
// Updating progress
var percentage = 0;
var progress = Controls.Progress("Processing", percentage);
for (var i = 0; i < 10; i++)
{
    await Task.Delay(30);
    area.UpdateProgress(
        new("ViewWithProgress"),
        progress = progress with { Progress = percentage += 10 }
    );
}
```

## Features
- Data binding with automatic updates
- Template-based rendering
- Interactive event handling
- Progress tracking
- Grid and collection support
- Composable control hierarchy

## Integration
- Framework-agnostic control definitions
- Supports multiple UI implementations
- Works with MeshWeaver messaging system
- Enables real-time updates

## See Also
- [MeshWeaver.Layout.Blazor](../MeshWeaver.Layout.Blazor/README.md) - Blazor implementation
- [Main MeshWeaver Documentation](../../Readme.md) - More about MeshWeaver architecture
