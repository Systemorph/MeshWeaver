---
Name: Graphical User Interface
Category: Documentation
Description: Build reactive UIs entirely from C# — controls, layout areas, data binding, and observables, with no frontend framework required.
Icon: /static/DocContent/GUI/icon.svg
---

<div style="background: linear-gradient(135deg, #00695c 0%, #00897b 100%); border-radius: 18px; padding: 40px 34px; margin: 4px 0 30px 0; color: #fff;">
  <div style="font-size: 2.1rem; font-weight: 800; letter-spacing: -0.02em; line-height: 1.15;">Graphical User Interface</div>
  <div style="font-size: 1.05rem; opacity: 0.92; margin-top: 10px; max-width: 720px; line-height: 1.55;">
    Build reactive user interfaces entirely in C#. Controls are immutable records, rendered server-side and streamed to the browser — no JavaScript framework, no manual DOM updates.
  </div>
</div>

# How it works

MeshWeaver's UI model has three interlocking ideas. Once they click, building rich interfaces is surprisingly straightforward.

## 1. Controls are immutable records

Every control is a plain C# record. `With*` methods return **new instances** — the original is never mutated:

```csharp
var button1 = Controls.Button("Click me");
var button2 = button1.WithId("myButton");  // button1 is unchanged
```

This means control definitions are pure data. You can safely share, copy, and compose them without worrying about side effects. A control doesn't *render* anything until it's placed in a layout area.

## 2. Rendering is declarative and area-based

Instead of describing how to update the DOM, you describe *what* each named area should show. Only the affected area re-renders when its data changes — sibling areas are never disturbed:

```csharp
Controls.Stack
    .WithView(Controls.Html("<h1>Dashboard</h1>"))      // Static — rendered once
    .WithView(liveDataStream.Select(d => ShowData(d)))  // Dynamic — updates on each emission
    .WithView(Controls.Button("Refresh"))               // Static — rendered once
```

Container structure is fixed; only observable-backed slots re-render.

## 3. Reactivity comes from `IObservable<T>`

Pass any `IObservable<T>` as a view and the content updates automatically. Subscriptions are created and disposed for you as areas mount and unmount:

```csharp
var counter = Observable.Interval(TimeSpan.FromSeconds(1));

Controls.Stack
    .WithView(counter.Select(n => Controls.Label($"Count: {n}")))
```

No callbacks to wire up. No teardown code to write.

---

## Live example

```csharp --render GuiOverviewDemo --show-code
Controls.Stack
    .WithView(Controls.Html("<b>MeshWeaver UI — live from the kernel</b>"))
    .WithView(Controls.Markdown($"Rendered at **{DateTime.Now:HH:mm:ss}** — controls are plain C# records."))
    .WithView(Controls.Button("A static button (no action wired here)"))
```

---

## Where to go next

Browse the full set of controls, layout primitives, and data-binding guides:

| Topic | Description |
|---|---|
| [Layout Areas](LayoutAreas) | Named rendering slots — how the hub decides what to show and where |
| [Container Controls](ContainerControl) | Stack, Tabs, Toolbar, Splitter — composing areas together |
| [Layout Grid](LayoutGrid) | CSS-grid-based two-dimensional layouts |
| [Data Binding](DataBinding) | Two-way reactive binding via JSON Pointers — the contract every backend area must follow |
| [Static vs. Dynamic Views](Observables) | When areas re-render and how to control update frequency |
| [DataGrid](DataGrid) | Tabular data with sorting, filtering, and row actions |
| [Editor](Editor) | Rich code and text editing controls |
| [Attributes](Attributes) | Declarative style, visibility, and validation annotations |
| [Side Panel](SidePanel) | Slide-in panels for detail views and settings |
| [Reactive Dialogs](ReactiveDialogs) | Modal dialogs backed by observable state |
| [Node Menu](NodeMenu) | Context menus on mesh nodes |
