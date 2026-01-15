---
Name: Static vs Dynamic Views
Category: Documentation
Description: Understanding when and how UI areas update in response to data changes
Icon: /static/storage/content/MeshWeaver/Documentation/GUI/Observables/icon.svg
---

MeshWeaver distinguishes between **static** views (rendered once) and **dynamic** views (re-rendered when observables emit). Understanding this distinction is key for efficient UIs.

## Static Views

A static view is rendered once and never updates:

```csharp
Controls.Stack
    .WithView(Controls.Label("Header"))         // Static - never changes
    .WithView(Controls.Button("Submit"))        // Static - never changes
```

**Characteristics:**
- Rendered once at initial load
- No subscriptions created
- Most efficient for unchanging content

## Dynamic Views

A dynamic view updates when its observable emits:

```csharp
Controls.Stack
    .WithView(counterStream.Select(n => Controls.Label($"Count: {n}")))
```

**Characteristics:**
- Creates a subscription to the observable
- Re-renders only that area on each emission
- Subscription disposed when area removed

## Combining Static and Dynamic

A typical pattern combines both:

```csharp
Controls.Stack
    .WithView(Controls.Html("<h1>Dashboard</h1>"))           // Static header
    .WithView(metricsStream.Select(m => BuildMetrics(m)))   // Dynamic content
    .WithView(Controls.Button("Refresh"))                    // Static button
```

The header and button never re-render. Only the middle area updates when `metricsStream` emits.

## How Re-rendering Works

When a dynamic view receives a new value:

1. Observable is subscribed with `DistinctUntilChanged()` to skip duplicates
2. Each emission calls `UpdateArea`
3. Existing views in that area are removed
4. New view is rendered
5. Only affected DOM elements update

```csharp
// This is what happens internally
generator
    .DistinctUntilChanged()
    .Subscribe(view => UpdateArea(context, view))
```

## Common Patterns

### Conditional Content

```csharp
isLoadingStream.Select(loading =>
    loading
        ? Controls.ProgressRing()
        : Controls.Label("Ready"))
```

### Computed Display

```csharp
dataStream.Select(data =>
    Controls.Markdown($"**Total:** {data.Items.Sum(i => i.Value)}"))
```

### Combined Streams

```csharp
userStream.CombineLatest(settingsStream, (user, settings) =>
    Controls.Label($"{user.Name} - {settings.Theme}"))
```

### Debounced Updates

```csharp
searchStream
    .Debounce(TimeSpan.FromMilliseconds(300))
    .Select(term => BuildSearchResults(term))
```

## Observable Operators

Common Rx operators for UI:

| Operator | Purpose | Example |
|----------|---------|---------|
| `Select` | Transform data to controls | `stream.Select(d => Controls.Label(d.Name))` |
| `DistinctUntilChanged` | Skip duplicate values | `stream.DistinctUntilChanged()` |
| `Debounce` | Reduce update frequency | `stream.Debounce(TimeSpan.FromMilliseconds(100))` |
| `CombineLatest` | Combine multiple streams | `a.CombineLatest(b, (x, y) => ...)` |
| `StartWith` | Provide initial value | `stream.StartWith(defaultValue)` |

## Performance Guidelines

**Use static for:**
- Headers, labels, navigation
- Action buttons with fixed text
- Content that doesn't change

**Use dynamic for:**
- Data displays
- Computed values
- Content dependent on user input

**Optimize with:**
- `DistinctUntilChanged()` to skip unchanged values
- `Debounce()` for high-frequency streams
- Minimal re-render scope (only the changing area)

## See Also

- [WithView Patterns](MeshWeaver/Documentation/GUI/ContainerControl) - All WithView overloads
- [DataBinding](MeshWeaver/Documentation/GUI/DataBinding) - How data flows to controls
- [Editor Control](MeshWeaver/Documentation/GUI/Editor) - Real-world dynamic examples
