---
Name: Graphical User Interface
Category: Documentation
Description: Build reactive UIs with controls, layout areas, and data binding
Icon: /static/storage/content/MeshWeaver/Documentation/GUI/icon.svg
---

MeshWeaver provides a control-based GUI system that renders reactive user interfaces from C# code.

---

## Patterns and Principles for Building Reactive UIs

### Key Principles

| Principle | Description |
|-----------|-------------|
| Immutable Controls | Controls are records - each `With*` method returns a new instance |
| Static Structure | Container structure is static; only area content can be dynamic |
| Observable Updates | Use observables to create reactive, updating UI areas |
| Declarative | Define what to render, not how to update it |

### View Patterns

- [Container Control](MeshWeaver/Documentation/GUI/ContainerControl) - Understanding the different `WithView` overloads for adding controls to containers

### Reactivity

- [Static vs Dynamic Views](MeshWeaver/Documentation/GUI/Observables) - Understanding when and how UI areas update in response to data changes

### Data Flow

- [Data Binding](MeshWeaver/Documentation/GUI/DataBinding) - How data flows through the UI with DataContext and UpdatePointer

### Configuration

- [Property Attributes](MeshWeaver/Documentation/GUI/Attributes) - Attributes for forms, validation, and control customization

---

## UI Controls

### Control Categories

| Category | Controls | Purpose |
|----------|----------|---------|
| Container | Stack, Tabs, Splitter, Layout | Organize other controls |
| Input | TextField, NumberField, CheckBox, Select | Accept user data |
| Display | Label, Badge, Icon, Markdown, Html | Show information |
| Data | DataGrid, Editor | Show and edit collections |
| Action | Button, MenuItem, NavLink | Trigger operations |

### Common Control Patterns

All controls share common patterns:

- **Immutable records**: Each `With*` method returns a new instance
- **Fluent API**: Chain methods for readable configuration
- **Data binding**: Connect controls to observable data streams

### Data Entry

- [Editor Control](MeshWeaver/Documentation/GUI/Editor) - Generate editable forms from C# records with automatic field rendering

### Layout

- [Stack Control](MeshWeaver/Documentation/GUI/Stack) - Arrange controls vertically or horizontally with configurable spacing

### Data Display

- [DataGrid Control](MeshWeaver/Documentation/GUI/DataGrid) - Display tabular data with sorting, filtering, and selection
