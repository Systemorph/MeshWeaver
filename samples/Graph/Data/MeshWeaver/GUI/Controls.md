---
Name: Controls
Category: Documentation
Description: Reference documentation for available UI controls
Icon: /static/storage/content/MeshWeaver/GUI/Controls/icon.svg
---

# Controls Reference

This section provides detailed documentation for the UI controls available in MeshWeaver.

## Available Controls

### Data Entry

- [Editor Control](MeshWeaver/GUI/Controls/Editor) - Generate editable forms from C# records with automatic field rendering

### Layout

- [Stack Control](MeshWeaver/GUI/Controls/Stack) - Arrange controls vertically or horizontally with configurable spacing

### Data Display

- [DataGrid Control](MeshWeaver/GUI/Controls/DataGrid) - Display tabular data with sorting, filtering, and selection

## Control Categories

| Category | Controls | Purpose |
|----------|----------|---------|
| **Container** | Stack, Tabs, Splitter, Layout | Organize other controls |
| **Input** | TextField, NumberField, CheckBox, Select | Accept user data |
| **Display** | Label, Badge, Icon, Markdown, Html | Show information |
| **Data** | DataGrid, Editor | Show and edit collections |
| **Action** | Button, MenuItem, NavLink | Trigger operations |

## Common Patterns

All controls share common patterns:

- **Immutable records**: Each `With*` method returns a new instance
- **Fluent API**: Chain methods for readable configuration
- **Data binding**: Connect controls to observable data streams
