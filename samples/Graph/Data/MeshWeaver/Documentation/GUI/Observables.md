---
Name: Static vs. Dynamic Views
Category: Documentation
Description: Understanding when and how UI areas update in response to data changes
Icon: /static/storage/content/MeshWeaver/Documentation/GUI/Observables/icon.svg
---

# Static vs. Dynamic Views

MeshWeaver distinguishes between **static** views (rendered once) and **dynamic** views (re-rendered when observables emit). Understanding this distinction is key for efficient UIs.

## Static Views

A static view is rendered once and never updates:

```csharp --render StaticExample --show-code
Controls.Stack                                              // Create a container
    .WithView(Controls.Html("<h2>Dashboard</h2>"))          // Static heading
    .WithView(Controls.Label("This text never changes"))    // Static label
```

---

**Characteristics:**
- Rendered once at initial load
- No subscriptions created
- Most efficient for unchanging content

---

## Dynamic Views

A dynamic view updates when its observable emits:

```csharp --render DynamicExample --show-code
var counter = Observable.Interval(TimeSpan.FromSeconds(1));  // Stream that emits every second

Controls.Stack                                                   // Create a container
    .WithView(Controls.Label("Page Title"))                      // Static - never changes
    .WithView(counter.Select(n => Controls.Label($"Count: {n}"))) // Dynamic - updates each second
```

---

**Characteristics:**
- Creates a subscription to the observable
- Re-renders only that area on each emission
- Subscription disposed when area removed

---

## Real-time Clock Example

```csharp --render Clock --show-code
var tick = Observable.Interval(TimeSpan.FromSeconds(1));  // Stream that emits every second

Controls.Stack                                                                  // Container
    .WithView(tick.Select(_ => Controls.Label(DateTime.Now.ToString("HH:mm:ss"))))  // Updates every second
```

---

## Combining Static and Dynamic

A typical pattern combines both:

```csharp
Controls.Stack
    .WithView(Controls.Html("<h1>Dashboard</h1>"))           // Static header
    .WithView(metricsStream.Select(m => BuildMetrics(m)))   // Dynamic content
    .WithView(Controls.Button("Refresh"))                    // Static button
```

The header and button never re-render. Only the middle area updates when `metricsStream` emits.

---

## Loading Data First

When you need to fetch data before showing content:

```csharp
Controls.Stack                                      // Create a container
    .WithView(async (host, ctx, ct) => {            // Async function - runs once when rendering
        var user = await LoadUserAsync(ct);         // Fetch user data from server
        return Controls.Label($"Hello, {user.Name}"); // Return control after data loaded
    })
```

---

## Reacting to Data Changes

When content depends on data in the store:

```csharp
Controls.Stack                                          // Create a container
    .WithView((host, ctx, store) =>                     // Function with access to data store
        host.GetDataStream<User>("currentUser")         // Get stream of user updates
            .Select(user => Controls.Label($"Logged in as {user.Name}")))  // Update when user changes
```

---

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

---

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

---

## Observable Operators

Common Rx operators for UI:

| Operator | Purpose | Example |
|----------|---------|---------|
| `Select` | Transform data to controls | `stream.Select(d => Controls.Label(d.Name))` |
| `DistinctUntilChanged` | Skip duplicate values | `stream.DistinctUntilChanged()` |
| `Debounce` | Reduce update frequency | `stream.Debounce(TimeSpan.FromMilliseconds(100))` |
| `CombineLatest` | Combine multiple streams | `a.CombineLatest(b, (x, y) => ...)` |
| `StartWith` | Provide initial value | `stream.StartWith(defaultValue)` |

---

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

---

## See Also

- [Container Control](MeshWeaver/Documentation/UserInterface/ContainerControl) - Adding content to containers
- [Data Binding](MeshWeaver/Documentation/UserInterface/DataBinding) - How data flows to controls
- [Editor Control](MeshWeaver/Documentation/UserInterface/Editor) - Real-world dynamic examples
