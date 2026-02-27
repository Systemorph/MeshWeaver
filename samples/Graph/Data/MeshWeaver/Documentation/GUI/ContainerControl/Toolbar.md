---
Name: Organizing Buttons in a Toolbar
Category: Documentation
Description: Group action buttons horizontally or vertically
Icon: /static/storage/content/MeshWeaver/Documentation/GUI/ContainerControl/Toolbar/icon.svg
---

The Toolbar control groups action buttons in a horizontal (default) or vertical layout, typically used for page headers and action bars.

# Basic Usage

```csharp --render ToolbarBasic --show-code
Controls.Toolbar                            // Create a toolbar
    .WithView(Controls.Button("New"))       // First action
    .WithView(Controls.Button("Edit"))      // Second action
    .WithView(Controls.Button("Delete"))    // Third action
```

---

# Vertical Toolbar

```csharp --render ToolbarVertical --show-code
Controls.Toolbar                                    // Create a toolbar
    .WithOrientation(Orientation.Vertical)          // Stack buttons vertically
    .WithView(Controls.Button("New"))               // Top button
    .WithView(Controls.Button("Edit"))              // Middle button
    .WithView(Controls.Button("Delete"))            // Bottom button
```

---

# Toolbar with Icons

```csharp --render ToolbarIcons --show-code
Controls.Toolbar                                                    // Create a toolbar
    .WithView(Controls.Button("").WithIconStart(FluentIcons.Add())) // Icon-only button
    .WithView(Controls.Button("").WithIconStart(FluentIcons.Edit()))// Icon-only button
    .WithView(Controls.Button("").WithIconStart(FluentIcons.Delete()))// Icon-only button
```

---

# Common Patterns

## Page Header Toolbar

```csharp --render ToolbarPageHeader --show-code
Controls.Stack                                          // Outer container
    .WithOrientation(Orientation.Horizontal)            // Horizontal layout
    .WithHorizontalAlignment("space-between")           // Space between title and toolbar
    .WithView(Controls.Html("<h2>Users</h2>"))          // Page title
    .WithView(
        Controls.Toolbar                                // Action buttons
            .WithView(Controls.Button("Add User"))      // Primary action
            .WithView(Controls.Button("Export"))        // Secondary action
    )
```

---

## Form Action Bar

```csharp --render ToolbarFormActions --show-code
Controls.Stack                                          // Form container
    .WithVerticalGap("16px")                            // Space between form and buttons
    .WithView(Controls.Label("Form content goes here")) // Placeholder for form
    .WithView(
        Controls.Toolbar                                // Action buttons at bottom
            .WithView(Controls.Button("Cancel"))        // Cancel action
            .WithView(Controls.Button("Save"))          // Save action
    )
```

---

# Configuration Methods

| Method | Purpose | Example |
|--------|---------|---------|
| `.WithOrientation(orientation)` | Button layout direction | `Orientation.Horizontal`, `Orientation.Vertical` |

---

# Skin Properties

The `ToolbarSkin` defines:

| Property | Type | Default |
|----------|------|---------|
| `Orientation` | `object?` | `Orientation.Horizontal` |

---

# See Also

- [Container Control](MeshWeaver/Documentation/UserInterface/ContainerControl) - Overview of all containers
- [Stack Control](MeshWeaver/Documentation/UserInterface/ContainerControl/Stack) - General-purpose layout container
