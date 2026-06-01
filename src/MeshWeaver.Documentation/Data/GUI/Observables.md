---
Name: Static vs. Dynamic Views
Category: Documentation
Description: How MeshWeaver UI areas decide when to re-render — and how to use observables to keep them live
Icon: /static/DocContent/GUI/Observables/icon.svg
---

MeshWeaver draws a clean line between **static** views (rendered once and forgotten) and **dynamic** views (re-rendered every time an observable emits). Getting this distinction right is the foundation of an efficient, responsive UI.

---

# Static Views

A static view is computed exactly once — when the layout area first loads. No subscription is created and no watcher is registered. This makes it the most efficient option for content that never needs to change.

```csharp --render StaticExample --show-code
Controls.Stack
    .WithView(Controls.Html("<h2>Dashboard</h2>"))
    .WithView(Controls.Label("This text never changes"))
```

> **Use static views for:** headers, navigation, action buttons with fixed labels, and any content that is determined entirely at render time.

---

# Dynamic Views

Wrap a control in an `IObservable<T>` and MeshWeaver subscribes to it. Every time the observable emits, only that area of the DOM is replaced — the rest of the page is untouched.

```csharp --render DynamicExample --show-code
var counter = Observable.Interval(TimeSpan.FromSeconds(1));

Controls.Stack
    .WithView(Controls.Label("Page Title"))                        // static — never changes
    .WithView(counter.Select(n => Controls.Label($"Count: {n}"))) // dynamic — updates each second
```

The subscription is created when the area is mounted and disposed automatically when it is removed. Only the specific area backed by the observable participates in the update cycle.

---

# Real-Time Clock Example

A live clock shows the pattern at its simplest — a single observable drives continuous DOM updates with no explicit state management:

```csharp --render Clock --show-code
var tick = Observable.Interval(TimeSpan.FromSeconds(1));

Controls.Stack
    .WithView(tick.Select(_ => Controls.Label(DateTime.Now.ToString("HH:mm:ss"))))
```

---

# Combining Static and Dynamic

In practice, most layout areas are a mix: structural scaffolding is static, and only the data-driven regions are reactive. This keeps subscriptions narrow and re-renders cheap.

```csharp
Controls.Stack
    .WithView(Controls.Html("<h1>Dashboard</h1>"))         // static header
    .WithView(metricsStream.Select(m => BuildMetrics(m))) // dynamic content
    .WithView(Controls.Button("Refresh"))                  // static button
```

The header and button are never touched by the update cycle. Only the middle area re-renders when `metricsStream` emits.

---

# Loading Data First

When you need to fetch data before producing a control, an `async` delegate runs once at render time and returns the result. The delegate is not a subscription — it fires exactly once:

```csharp
Controls.Stack
    .WithView(async (host, ctx, ct) => {
        var user = await LoadUserAsync(ct);
        return Controls.Label($"Hello, {user.Name}");
    })
```

---

# Reacting to Data Changes

When the content depends on a data stream, expose that stream directly via `WithView`. The area subscribes and stays in sync for as long as it is mounted:

```csharp
Controls.Stack
    .WithView((host, ctx, store) =>
        host.GetDataStream<User>("currentUser")
            .Select(user => Controls.Label($"Logged in as {user.Name}")))
```

---

# How Re-rendering Works

Understanding the internals helps avoid surprises. When a dynamic view receives a new value, the framework:

1. Subscribes to the observable with `DistinctUntilChanged()` to skip identical emissions.
2. Calls `UpdateArea` on each new value.
3. Removes the existing controls in that area.
4. Renders the new control in their place.
5. Writes only the affected DOM elements to the client.

```csharp
// Framework internals — shown for clarity, not for direct use
generator
    .DistinctUntilChanged()
    .Subscribe(view => UpdateArea(context, view))
```

The key insight is that only the area backed by the emitting observable updates. Everything else on the page remains untouched.

---

# Common Patterns

## Conditional Content

Toggle between controls based on a boolean stream:

```csharp
isLoadingStream.Select(loading =>
    loading
        ? Controls.ProgressRing()
        : Controls.Label("Ready"))
```

## Computed Display

Derive a rendered value from a data stream in a single expression:

```csharp
dataStream.Select(data =>
    Controls.Markdown($"**Total:** {data.Items.Sum(i => i.Value)}"))
```

## Combined Streams

Combine two independent streams into a single view — the area updates whenever either stream emits:

```csharp
userStream.CombineLatest(settingsStream, (user, settings) =>
    Controls.Label($"{user.Name} - {settings.Theme}"))
```

## Debounced Updates

Throttle high-frequency input before building controls:

```csharp
searchStream
    .Debounce(TimeSpan.FromMilliseconds(300))
    .Select(term => BuildSearchResults(term))
```

---

# Observable Operators for UI

These Rx operators appear most often in MeshWeaver UI code:

| Operator | Purpose | Typical use |
|---|---|---|
| `Select` | Transform data to a control | `stream.Select(d => Controls.Label(d.Name))` |
| `DistinctUntilChanged` | Skip duplicate emissions | `stream.DistinctUntilChanged()` |
| `Debounce` | Reduce update frequency | `stream.Debounce(TimeSpan.FromMilliseconds(100))` |
| `CombineLatest` | Merge two streams into one view | `a.CombineLatest(b, (x, y) => ...)` |
| `StartWith` | Provide an initial value before the first emission | `stream.StartWith(defaultValue)` |

---

# Performance Guidelines

The most important rule: keep dynamic regions small. A large re-render is always more expensive than a small one, even if the data-fetch is fast.

| Scenario | Recommendation |
|---|---|
| Headers, labels, navigation, buttons with fixed text | Static view — no subscription overhead |
| Data displays, computed values, user-input-dependent content | Dynamic view — scoped to only the changing area |
| High-frequency streams (e.g. search input) | Add `Debounce()` to reduce render churn |
| Streams that frequently re-emit the same value | Add `DistinctUntilChanged()` to skip no-op renders |

---

# Click Actions Are Reactive, Not Async

> **🚨 ABSOLUTE:** Never use `await` inside a `WithClickAction` handler. The handler runs inside the layout hub's message pump. Awaiting a mesh-backed service inside the pump deadlocks it. Compose `IObservable<T>` chains instead and call `Subscribe`.

```csharp
// ✅ Correct — synchronous handler, observable chain for the async work
.WithClickAction(ctx =>
{
    ctx.Host.UpdateData(statusId, "<p>Working…</p>");  // immediate feedback

    ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
        .Take(1)
        .Subscribe(data =>
        {
            var input = data?.GetValueOrDefault("field")?.ToString() ?? "";

            myService.DoReactive(input).Subscribe(
                result => ctx.Host.UpdateData(statusId, $"<p>Done: {result}</p>"),
                ex     => ctx.Host.UpdateData(statusId, $"<p>Error: {ex.Message}</p>"));
        });

    return Task.CompletedTask;  // the handler itself is synchronous
})

// ❌ Wrong — async handler deadlocks the pump under load
.WithClickAction(async ctx =>
{
    var data = await ctx.Host.Stream.GetDataStream<T>(id).FirstAsync();
    var result = await myService.DoWorkAsync(data);
    ctx.Host.UpdateData(statusId, result);
})
```

See [AsynchronousCalls](../../Architecture/AsynchronousCalls) for the full rationale and additional patterns.

---

# See Also

- [Container Control](../ContainerControl) — Adding content to containers
- [Data Binding](../DataBinding) — How data flows to controls
- [Editor Control](../Editor) — Real-world dynamic examples
- [Asynchronous Calls](../../Architecture/AsynchronousCalls) — Why `await` deadlocks and how to compose observables
