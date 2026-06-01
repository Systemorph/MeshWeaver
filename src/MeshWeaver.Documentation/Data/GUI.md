---
Name: Graphical User Interface
Category: Documentation
Description: Build reactive UIs entirely from C# — controls, layout areas, data binding, and observables, with no frontend framework
Icon: /static/DocContent/GUI/icon.svg
---

<div style="background: linear-gradient(135deg, #00695c 0%, #00897b 100%); border-radius: 18px; padding: 40px 34px; margin: 4px 0 30px 0; color: #fff;">
  <div style="font-size: 2.1rem; font-weight: 800; letter-spacing: -0.02em; line-height: 1.15;">Graphical User Interface</div>
  <div style="font-size: 1.05rem; opacity: 0.92; margin-top: 10px; max-width: 720px; line-height: 1.55;">
    Build reactive user interfaces from C# code. Controls are immutable records, rendered server-side and streamed to the browser — no JavaScript framework required.
  </div>
</div>

# How it works

## Immutable controls

Every control is a C# record. Calling a `With*` method returns a **new instance** — the original is unchanged:

```csharp
var button1 = Controls.Button("Click me");
var button2 = button1.WithId("myButton");  // button1 is unchanged
```

You can safely reuse and compose controls without side effects. A control definition is just data — it doesn't *do* anything until rendered.

## Declarative, area-based rendering

Define *what* to show, not *how* to update it. The UI is divided into named areas, and only the affected area re-renders:

```csharp
Controls.Stack
    .WithView(Controls.Html("<h1>Dashboard</h1>"))     // Static — never re-renders
    .WithView(liveDataStream.Select(d => ShowData(d))) // Dynamic — updates when the stream emits
    .WithView(Controls.Button("Refresh"))              // Static — never re-renders
```

Container structure is static; only area content is dynamic. Changing one area never disturbs its siblings.

## Observable-driven reactivity

Pass an `IObservable<T>` and the content updates automatically — each emission replaces the area, and subscriptions are disposed for you when the area is removed:

```csharp
var counter = Observable.Interval(TimeSpan.FromSeconds(1));

Controls.Stack
    .WithView(counter.Select(n => Controls.Label($"Count: {n}")))
```

---

Browse the full set of controls, layout primitives, and data-binding guides below.
