---
Name: WithView Patterns
Category: Documentation
Description: Understanding the different WithView overloads for adding controls to containers
Icon: /static/storage/content/MeshWeaver/GUI/Concepts/WithView/icon.svg
---

The `WithView` method is the primary way to add child controls to container controls like `Stack`, `Tabs`, or `LayoutGrid`. Understanding its overloads is key to building reactive UIs.

## Source Files

| File | Purpose |
|------|---------|
| `src/MeshWeaver.Layout/ContainerControl.cs` | All WithView overloads |

## Overview

Container controls have multiple `WithView` overloads that differ in **when** and **how** the child control is evaluated:

| Pattern | Signature | Evaluation | Updates |
|---------|-----------|------------|---------|
| Static | `WithView(UiControl)` | Once | Never |
| Sync Function | `WithView((host, ctx) => UiControl)` | Once at render | Never |
| Async Function | `WithView(async (host, ctx, ct) => Task<UiControl>)` | Once (awaited) | Never |
| Observable | `WithView(IObservable<UiControl>)` | On each emission | Yes |
| ViewStream | `WithView((host, ctx, store) => IObservable<T>)` | Subscribe once | On each emission |

## Static Views

Pass a control directly (ContainerControl.cs:94-97):

```csharp
Controls.Stack
    .WithView(Controls.Label("Hello"))
    .WithView(Controls.Button("Click"))
```

**Use for:** Fixed content like headers, labels, buttons with static text.

## Sync Function Views

Pass a function that returns a control (ContainerControl.cs:167-168):

```csharp
Controls.Stack
    .WithView((host, ctx) => Controls.Label($"Area: {ctx.Area}"))
    .WithView((host, ctx) => BuildContent(host.Hub))
```

**Use for:** Content that needs context at render time but doesn't update.

## Async Function Views

Pass an async function (ContainerControl.cs:169-170):

```csharp
Controls.Stack
    .WithView(async (host, ctx, ct) => {
        var data = await FetchDataAsync(ct);
        return Controls.Label(data.Name);
    })
```

**Use for:** Content that requires awaiting data before rendering.

## Observable Views

Pass an `IObservable<UiControl>` (ContainerControl.cs:145-163):

```csharp
Controls.Stack
    .WithView(dataStream.Select(d => Controls.Label($"Count: {d.Count}")))
```

**Use for:** Content that updates when external data changes.

### Example: Real-time Counter

```csharp
var counter = Observable.Interval(TimeSpan.FromSeconds(1));

Controls.Stack
    .WithView(counter.Select(n => Controls.Label($"Seconds: {n}")))
```

## ViewStream Delegate

Pass a function returning an observable (ContainerControl.cs:175-193):

```csharp
Controls.Stack
    .WithView((host, ctx, store) =>
        host.GetDataStream<MyData>("id")
            .Select(d => Controls.Label(d.Name)))
```

**Use for:** Content that depends on data from the stream. You have access to:
- `LayoutAreaHost` - for hub, streams, services
- `RenderingContext` - for area information
- `EntityStore` - for current data state

### Example: Data-Dependent View

```csharp
Controls.Stack
    .WithView((host, ctx, store) => {
        var userId = store.GetData<string>("currentUserId");
        return host.GetDataStream<User>(userId)
            .Select(user => Controls.Label($"Hello, {user.Name}"));
    })
```

## Named Areas

All overloads support naming the area:

```csharp
// With string name
.WithView(Controls.Label("Text"), "myArea")

// With options function
.WithView(Controls.Label("Text"), opt => opt.WithId("myArea"))
```

Named areas are useful for:
- Targeting specific areas with updates
- Debugging the control tree
- Referencing areas from other UI parts

## Choosing the Right Pattern

| Scenario | Pattern |
|----------|---------|
| Fixed text, icons, static buttons | Static |
| Need context at render time | Sync Function |
| Need to await data before rendering | Async Function |
| Content changes based on external events | Observable |
| Content changes based on stream data | ViewStream |

## Subscription Lifecycle

When using observable patterns (LayoutAreaHost.cs:251-254):

1. Observable subscribed when area first renders
2. Each emission updates only that area
3. Subscription disposed when area removed or parent re-renders

This is managed automatically by `RegisterForDisposal`.

## See Also

- [Observables](MeshWeaver/GUI/Concepts/Observables) - Deep dive into static vs dynamic views
- [DataBinding](MeshWeaver/GUI/Concepts/DataBinding) - How data flows through the UI
- [Stack Control](MeshWeaver/GUI/Controls/Stack) - Container using WithView
