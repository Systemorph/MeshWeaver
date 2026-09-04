---
Name: Custom Blazor Controls
Category: Documentation
Description: Extend the Blazor portal with your own control — define a UiControl subclass server-side, write a BlazorView that data-binds it, and register the pair with WithView. Ships three ways, including from a plugin at runtime.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5"><rect x="3" y="4" width="18" height="14" rx="2"/><path d="M3 9h18"/><path d="M8 13h5"/><path d="M8 4v5"/></svg>
---

# Custom Blazor Controls

The Blazor portal renders a **`UiControl` tree**: a layout area is delivered as one JSON object with an
`areas` map (area key → control) and a `data` map (values bindings point at), updated via RFC 7396
merge-patches. Every control carries a `$type` discriminator, and the renderer dispatches on it to a
Blazor component.

So extending the portal has exactly two halves — the same two the
[React renderer](../ReactCustomControls) has:

1. **Server side** — a `UiControl` subclass registered with the hub, so layout areas can emit it.
2. **Blazor side** — a component registered as the view for that control type.

> **Reach for this last.** A control that composes existing controls (`Controls.Stack`,
> `DataGrid`, `Controls.Markdown`) works in **every** renderer for free. A custom Blazor view works
> only in Blazor — the React and MAUI clients render the same tree and will not know your `$type`.
> Write one when you genuinely need JS interop or a third-party component library; see
> [Cross-Renderer Authoring](../CrossRendererAuthoring).

## 1. Define the control server-side

A control is a record. It carries data, never rendering:

```csharp
public record HeatmapControl(object Values) : UiControl<HeatmapControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public object? ColorScale { get; init; }
    public HeatmapControl WithColorScale(object scale) => this with { ColorScale = scale };
}
```

Properties are `object?` rather than concrete types on purpose: a bound value arrives as a JSON
pointer to the `data` map, not the value itself, and is resolved per render.

## 2. Write the view

Views derive from `BlazorView<TViewModel, TView>`, which supplies the hub, the synchronization
stream, the area path, theme and data-context cascades, and disposal. Override `BindData` to wire
each control property to a field:

```csharp
public partial class HeatmapView : BlazorView<HeatmapControl, HeatmapView>
{
    private object? Values { get; set; }
    private object? ColorScale { get; set; }

    protected override void BindData()
    {
        base.BindData();                       // 🚨 never skip — binds Id, Class, Style
        DataBind(ViewModel.Values, x => x.Values);
        DataBind(ViewModel.ColorScale, x => x.ColorScale);
    }
}
```

`DataBind` resolves a JSON pointer against the stream and re-renders on change; it takes an optional
conversion for values that arrive as `JsonElement`. **Deserialize with
`Stream.Hub.JsonSerializerOptions`**, never a fresh `JsonSerializerOptions` — the hub's instance is
the one carrying the `$type` registry, and without it a polymorphic payload degrades to a raw
`JsonElement` and the view renders empty.

The `.razor` half is an ordinary component:

```razor
@inherits BlazorView<HeatmapControl, HeatmapView>

@if (Values is not null)
{
    <div class="@Class" style="@Style">@* … *@</div>
}
```

## 3. Register the pair

One line, on the hub configuration:

```csharp
public static MessageHubConfiguration AddHeatmap(this MessageHubConfiguration config) =>
    config.WithType(typeof(HeatmapControl))
        .AddViews(layout => layout.WithView<HeatmapControl, HeatmapView>());
```

🚨 **`WithType` is not optional, and it is needed on every hub the control crosses.** The control
travels as JSON between the hub that emits it and the hub that renders it; a hub whose `TypeRegistry`
lacks the discriminator hands the renderer an untyped `JsonElement` instead of a `HeatmapControl`. The
symptom is not an error — the area renders empty, or reports that it cannot be found.

## 4. Ship it — three ways

| Where the view lives | How it registers | When |
|---|---|---|
| Core (`MeshWeaver.Blazor`) | in the portal's own composition | platform controls |
| A compiled **view pack** (a plain class library) | its `Add…Views()` entry point, called at startup | third-party component libraries — `MeshWeaver.Blazor.Radzen` is the reference |
| A **plugin**, at runtime | `WithPortalConfiguration` from the plugin's own hub config | a module shipping its own UI |

The third is the one that needs explanation. A plugin's assembly is compiled and loaded at NodeType
activation, long after the layout client was configured — and the portal hub is a *different* hub
(one per browser circuit), so returning a modified config cannot reach it. The delegate is routed
instead:

```csharp
// A NodeType's `configuration` lambda — it configures THIS node's hub; the portal is elsewhere.
config => config
    .WithType(typeof(HeatmapControl))
    .WithPortalConfiguration(portal => portal
        .WithType(typeof(HeatmapControl))
        .AddViews(layout => layout.WithView<HeatmapControl, HeatmapView>()))
```

From the portal's side nothing is special — it is the same `WithView` seam the packs use. See
[UI Extensibility](/Doc/Architecture/UiExtensibility) for the registry behind it.

## What to know before shipping a runtime view

Each of these is invisible when it bites, which is why they are listed rather than left to discovery:

- **A runtime-loaded assembly brings no static web assets.** An RCL's `wwwroot` is served from a
  **build-time** manifest at `_content/<lib>/…`, and `.razor.css` is bundled into
  `<project>.styles.css` at build. Neither exists for an assembly the image was not built with, so a
  runtime-contributed view must carry no CSS/JS of its own — inline what it needs, or ship it as a
  compiled pack. (A pack can self-load its script on first render and gate rendering on it, as
  `RadzenChartView` does with `AssetsReady`.)
- **A third-party component's root element is not yours to style — unless you ship the rule.** A
  view that delegates to a component library renders *its* markup, and that markup may carry no
  `style` attribute at all: BlazorMonaco, for one, emits its editor host as
  `<div id="…" class="@CssClass">` and nothing else. Wrapping it in a sized `<div>` does not help —
  `height` does not inherit and a block child does not stretch — so the component's own root
  collapses to zero pixels and the control renders as an empty gap: no exception, no console
  warning, no failed request, just nothing on screen. Name a class, ship a `::deep` rule for it in
  the view's `.razor.css`, and check the rendered box, not the emitted markup. This is what made a
  node's version comparison invisible for months (MeshWeaver#3288); the guard that now pins it is
  `MonacoEditorContainerSizingGuard`, beside the views in MeshWeaver.Plugins.
- **A recompile mints a new type.** Every NodeType rebuild loads into a fresh collectible
  `AssemblyLoadContext`, so "the same" view class is a different CLR type per build. Portal
  contributions are keyed by owner and **replace** on re-registration for exactly this reason;
  anything else you hold onto across a rebuild pins the old context against unload.
- **A contribution applies to the next portal hub.** A portal hub is configured once, at circuit
  creation, so a plugin installed mid-session takes effect on the viewer's next page load.
- **Other renderers will not have your control.** React and MAUI dispatch on the same `$type` and
  need their own component for it — [Custom React Controls](../ReactCustomControls) is the other half.

## Related

[Cross-Renderer Authoring](../CrossRendererAuthoring) ·
[Custom React Controls](../ReactCustomControls) ·
[Data Binding](../DataBinding) ·
[Display Controls](../DisplayControls) ·
[UI Extensibility](/Doc/Architecture/UiExtensibility)
