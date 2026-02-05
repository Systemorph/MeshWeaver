---
Name: Graphical User Interface
Category: Documentation
Description: Build reactive UIs with controls, layout areas, and data binding
Icon: /static/storage/content/MeshWeaver/Documentation/GUI/icon.svg
---

# Graphical User Interface

MeshWeaver provides a control-based GUI system that renders reactive user interfaces from C# code.

---

## What do you want to do?

| I want to... | Go here |
|--------------|---------|
| Arrange controls on screen | [Container Controls](MeshWeaver/Documentation/GUI/ContainerControl) - Stack, Tabs, Toolbar |
| Build responsive layouts | [Layout Grid](MeshWeaver/Documentation/GUI/LayoutGrid) - Adapt to phones, tablets, desktops |
| Display data in a table | [DataGrid](MeshWeaver/Documentation/GUI/DataGrid) - Sortable columns, pagination |
| Create an editable form | [Editor](MeshWeaver/Documentation/GUI/Editor) - Auto-generate forms from records |
| Make content update automatically | [Static vs Dynamic Views](MeshWeaver/Documentation/GUI/Observables) - Observables, reactivity |
| Control how data flows | [Data Binding](MeshWeaver/Documentation/GUI/DataBinding) - DataContext, UpdatePointer |
| Customize field behavior | [Attributes](MeshWeaver/Documentation/GUI/Attributes) - Validation, display options |
| Build complex interactive dialogs | [Reactive Dialogs](MeshWeaver/Documentation/GUI/ReactiveDialogs) - Subjects, streaming computation |

---

## How it works

### Immutable Controls

Every control is a C# record. When you call a `With*` method, you get a **new instance** - the original is unchanged:

```csharp
var button1 = Controls.Button("Click me");
var button2 = button1.WithId("myButton");  // button1 is unchanged

// button1 and button2 are different objects
```

**Why this matters:** You can safely reuse and compose controls without side effects. A control definition is just data - it doesn't "do" anything until rendered.

---

### Fluent API

Chain methods for readable configuration:

```csharp
Controls.Stack
    .WithOrientation(Orientation.Horizontal)
    .WithHorizontalGap("8px")
    .WithView(Controls.Button("Save"))
    .WithView(Controls.Button("Cancel"))
```

**Why this matters:** Order of `With*` calls doesn't matter (each is independent). Every chain produces a complete control definition ready to render.

---

### Declarative Rendering

Define *what* to show, not *how* to update it:

```csharp
// You declare the UI structure
Controls.Stack
    .WithView(Controls.Label($"Hello, {user.Name}"))
    .WithView(Controls.Button("Logout"))

// The framework handles rendering to the DOM
```

**Why this matters:** No manual DOM manipulation. You describe the desired state, MeshWeaver figures out how to make it happen.

---

### Area-based Updates

The UI is divided into named areas. Only the affected area re-renders, not the whole UI:

```csharp
Controls.Stack
    .WithView(Controls.Html("<h1>Dashboard</h1>"))     // Static - never re-renders
    .WithView(liveDataStream.Select(d => ShowData(d))) // Dynamic - updates when stream emits
    .WithView(Controls.Button("Refresh"))              // Static - never re-renders
```

**Why this matters:** Efficient updates. Changing one area doesn't affect siblings. Container structure is static; only area content can be dynamic.

---

### Observable-driven Reactivity

Pass an `IObservable<T>` to make content update automatically:

```csharp
var counter = Observable.Interval(TimeSpan.FromSeconds(1));

Controls.Stack
    .WithView(counter.Select(n => Controls.Label($"Count: {n}")))
```

**Why this matters:** Each emission replaces the area content. Subscriptions are managed automatically - disposed when the area is removed. No manual subscription handling needed.
