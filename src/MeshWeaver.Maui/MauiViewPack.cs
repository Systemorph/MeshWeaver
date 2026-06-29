using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.GoogleMaps;
using MeshWeaver.Graph;
using MeshWeaver.Kernel;
using MeshWeaver.Maui.Abstractions;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Pivot;
using MeshWeaver.Layout.Views;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace MeshWeaver.Maui;

/// <summary>
/// Maps a <see cref="UiControl"/> type to the native-MAUI <see cref="MauiView"/> that renders it — the
/// AspNetCore-free counterpart of MeshWeaver.Blazor's <c>BlazorViewRegistry</c>. Registered as a singleton
/// (instance, mesh-scoped) and consumed by <see cref="MauiControlRenderer"/>.
/// </summary>
public sealed class MauiViewRegistry
{
    private readonly Dictionary<Type, Type> _map = new();

    public MauiViewRegistry Register<TControl, TView>()
        where TControl : UiControl
        where TView : MauiView, new()
    {
        _map[typeof(TControl)] = typeof(TView);
        return this;
    }

    public Type? Resolve(UiControl control)
    {
        if (_map.TryGetValue(control.GetType(), out var t)) return t;
        // Any container (Stack/LayoutGrid/Layout/Toolbar/…) renders its child areas generically.
        if (control is IContainerControl) return typeof(ContainerView);
        return null;
    }
}

/// <summary>Builds native views from control instances and renders named child areas reactively.</summary>
public interface IMauiControlRenderer
{
    View RenderControl(UiControl control, ISynchronizationStream<JsonElement>? stream, string area);

    /// <summary>A view that subscribes to <paramref name="area"/>'s control stream and swaps its content.</summary>
    View RenderArea(ISynchronizationStream<JsonElement> stream, string area);
}

public sealed class MauiControlRenderer(MauiViewRegistry registry) : IMauiControlRenderer
{
    public View RenderControl(UiControl control, ISynchronizationStream<JsonElement>? stream, string area)
    {
        var viewType = registry.Resolve(control);
        if (viewType is null)
            return new Label { Text = $"[no MAUI view for {control.GetType().Name}]" };

        var view = (MauiView)Activator.CreateInstance(viewType)!;
        view.Initialize(control, stream, area, this);
        return view.View;
    }

    public View RenderArea(ISynchronizationStream<JsonElement> stream, string area)
    {
        // A spinner until the area emits its first control (replaced on emission). Also gives the host a
        // non-zero desired size, so the region is visible while resolving rather than collapsing to nothing.
        var host = new ContentView
        {
            Content = new ActivityIndicator
            {
                IsRunning = true,
                HeightRequest = 24,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(0, 12),
            },
        };
        IDisposable? sub = null;

        // 🚨 Once THIS region has rendered a control, we NEVER replace it with an error or spinner. A
        // layout-area stream stays open and goes QUIET after delivering its content, so a lifetime
        // timeout (the old `.Timeout(20s)`) would later fire on a perfectly-rendered area and wipe it —
        // and for the page's ROOT area that wiped the WHOLE page ("area didn't resolve" after a while).
        // Errors/timeouts are scoped to this host and only surface while the region is still empty.
        var rendered = false;
        IDisposable? deadline = null;

        void ShowNotLoaded()
        {
            if (rendered) return;   // keep the last good content — don't clobber a region that loaded
            host.Content = new Label
            {
                Text = "⚠ couldn't load this section",
                TextColor = Colors.OrangeRed, FontSize = 11, Margin = new Thickness(8),
                LineBreakMode = LineBreakMode.WordWrap,
            };
        }

        // INITIAL-LOAD deadline only: if no control arrives within 20s, show the in-region notice
        // instead of an eternal spinner. Cancelled the instant the first control renders, so it can
        // never fire on an already-loaded (but quiet) area. NOT a retry/resubscribe watchdog.
        deadline = Observable.Timer(TimeSpan.FromSeconds(20))
            .Subscribe(_ => MainThread.BeginInvokeOnMainThread(ShowNotLoaded));

        // Tie the area subscription to the host's Loaded/Unloaded lifecycle. MAUI fires Unloaded
        // SPURIOUSLY during nested-layout/handler changes; GetControlStream replays the current control
        // to a fresh subscriber, so a torn-down subscription would miss it. Subscribe ONCE and stay
        // subscribed; dispose only when the host is finally removed AND not re-added (deferred check).
        sub = stream.GetControlStream(area)
            // Retry transient area errors with backoff until the (possibly nested/remote) area resolves —
            // the SAME shared helper Blazor's NamedAreaView uses.
            .RetryAreaWithBackoff(AreaErrorClassifier.ShouldRetryArea)
            .Subscribe(
                ctrl => MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (ctrl is not UiControl c) return;   // ignore empty frames; keep current content
                    rendered = true;
                    deadline?.Dispose();                   // first content in → stop the load deadline
                    host.Content = RenderControl(c, stream, area);
                }),
                // A stream error AFTER content rendered is kept silent (we keep the good content); only an
                // error before first render surfaces the in-region notice.
                _ => MainThread.BeginInvokeOnMainThread(ShowNotLoaded));
        host.Unloaded += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (host.Parent is null) { sub?.Dispose(); sub = null; deadline?.Dispose(); }   // only if not re-parented
        });
        return host;
    }
}

/// <summary>
/// Base for a native-MAUI renderer of one <see cref="UiControl"/> — the MAUI counterpart of
/// MeshWeaver.Blazor's <c>BlazorView</c>. Builds a native <see cref="View"/>, binds the control's
/// (possibly data-bound) properties to the layout-area stream, and disposes its subscriptions.
/// </summary>
public abstract class MauiView : IDisposable
{
    protected readonly List<IDisposable> Disposables = new();

    public UiControl Control { get; private set; } = null!;
    public ISynchronizationStream<JsonElement>? Stream { get; private set; }
    public string Area { get; private set; } = "";
    public IMauiControlRenderer Renderer { get; private set; } = null!;

    internal void Initialize(UiControl control, ISynchronizationStream<JsonElement>? stream, string area,
        IMauiControlRenderer renderer)
    {
        Control = control;
        Stream = stream;
        Area = area;
        Renderer = renderer;
    }

    private View? _view;
    public View View => _view ??= BuildAndBind();

    private View BuildAndBind()
    {
        var v = CreateView();
        Bind();
        return v;
    }

    /// <summary>Creates the native view shell (no data yet).</summary>
    protected abstract View CreateView();

    /// <summary>Subscribe the control's bound properties to the stream. Override per control.</summary>
    protected virtual void Bind() { }

    /// <summary>
    /// Binds a control property to a setter. If the value is a <see cref="JsonPointerReference"/> it
    /// subscribes to the stream (mirrors <c>BlazorView.DataBind</c>); otherwise it sets the literal. Updates
    /// are applied on the UI thread.
    /// </summary>
    protected void Bind<T>(object? value, Action<T?> setter, T? defaultValue = default)
    {
        if (value is JsonPointerReference reference && Stream is not null)
            Disposables.Add(Stream.DataBind<T>(reference, Control.DataContext, defaultValue: defaultValue)
                .Subscribe(v => MainThread.BeginInvokeOnMainThread(() => setter(v))));
        else if (value is T t)
            setter(t);
        else if (value is null)
            setter(defaultValue);
        else
            try { setter((T?)Convert.ChangeType(value, typeof(T))); } catch { setter(defaultValue); }
    }

    /// <summary>
    /// Dispatches a click for this control's area as a <see cref="ClickedEvent"/> to the stream owner —
    /// the server-side <c>ClickAction</c> delegate does NOT serialize to the client, so a click is a
    /// message that the layout area receives and turns into the action. Mirrors <c>BlazorView.OnClick</c>,
    /// incl. stamping the (device-)user <see cref="AccessContext"/> so the owning hub's access gate doesn't
    /// deny the downstream write. Shared by Button / NavLink / MenuItem / any clickable container.
    /// </summary>
    protected void PostClick(object? payload = null)
    {
        if (Stream is null) return;
        var access = Stream.Hub.ServiceProvider.GetService<AccessService>();
        var ctx = access?.Context ?? access?.CircuitContext;
        var evt = payload is null
            ? new ClickedEvent(Area, Stream.StreamId)
            : new ClickedEvent(Area, Stream.StreamId) { Payload = payload };
        Stream.Hub.Post(evt, o => ctx is not null
            ? o.WithTarget(Stream.Owner).WithAccessContext(ctx)
            : o.WithTarget(Stream.Owner));
    }

    public void Dispose()
    {
        foreach (var d in Disposables) d.Dispose();
        Disposables.Clear();
    }
}

public abstract class MauiView<TControl> : MauiView where TControl : UiControl
{
    protected TControl Model => (TControl)Control;
}

/// <summary>
/// Base for two-way form controls (TextField/Checkbox/Switch/…): reads the bound value from the stream and
/// writes native edits back via <c>UpdatePointer</c>. Echo-suppressed — a write triggered by our own
/// programmatic set is ignored, so the stream's echo can't fight the user's typing (mirrors
/// MeshWeaver.Blazor's FormComponentBase).
/// </summary>
public abstract class FormMauiView<TControl> : MauiView<TControl> where TControl : UiControl
{
    private bool _suppress;

    /// <summary>Read the bound value into the native control, suppressing the write-back echo.</summary>
    protected void BindValue<T>(object? boundValue, Action<T?> setNative)
        => Bind<T>(boundValue, v => { _suppress = true; try { setNative(v); } finally { _suppress = false; } });

    /// <summary>Write a native edit back to the bound pointer (no-op during a programmatic set).</summary>
    protected void Write<T>(object? boundValue, T? value)
    {
        if (_suppress) return;
        if (boundValue is JsonPointerReference reference && Stream is not null)
            Stream.UpdatePointer(value, Control.DataContext, reference);
    }

    /// <summary>
    /// Coerces a list control's Options into (display text, value-string) pairs the native picker/list
    /// can show + select — delegates to the MAUI-free <see cref="MauiOptionCoercion"/> (the single,
    /// unit-tested implementation; handles both a typed <see cref="Option"/> list and the JsonElement
    /// array Options take after the layout-stream round-trip).
    /// </summary>
    protected static List<(string Text, string? Value)> CoerceOptions(object? options)
        => MauiOptionCoercion.Coerce(options);
}

/// <summary>
/// The public host: renders a mesh layout area (a control tree) as native MAUI, reactively.
/// <list type="bullet">
/// <item>The <c>(workspace, reference)</c> ctor renders an area served by the LOCAL hub (e.g. a custom
/// <c>AddLayout</c> area).</item>
/// <item>The <c>(workspace, address, reference)</c> ctor renders a REMOTE area served at a node's
/// <see cref="Address"/> — e.g. an <c>AddGraph</c> node area like <c>Overview</c> at the node's path —
/// via <see cref="WorkspaceExtensions.GetRemoteStream{TReduced,TReference}"/>, exactly as the Blazor
/// portal's <c>LayoutAreaView</c> does (<c>GetRemoteStream&lt;JsonElement, LayoutAreaReference&gt;(Address, Reference)</c>).</item>
/// </list>
/// </summary>
public sealed class LayoutAreaView : ContentView
{
    // Local area: GetStream(ref).Reduce("/") — exactly the Blazor LayoutAreaView's isLocal branch. (The
    // address ctor below is for a remote node's area via GetRemoteStream.)
    public LayoutAreaView(IWorkspace workspace, LayoutAreaReference reference, IMauiControlRenderer renderer)
    {
        var stream = workspace.GetStream(reference)!.Reduce(new JsonPointerReference("/"))!;
        Content = renderer.RenderArea(stream, reference.Area);
    }

    public LayoutAreaView(IWorkspace workspace, Address address, LayoutAreaReference reference, IMauiControlRenderer renderer)
    {
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, reference)!;
        Content = renderer.RenderArea(stream, reference.Area);
    }
}

/// <summary>
/// Mesh-scoped holder for the per-user "real" sub-hub (<c>portal/device-user</c>) that the native
/// interactive-markdown view starts kernel activities FROM — NEVER the top-level mesh hub (an activity
/// created from the root hub is ownerless/unroutable and storms the router; activities must always start
/// from a real sub-hub). Populated once at startup in <c>MauiProgram</c>; read by
/// <see cref="MauiCollaborativeMarkdownView"/>. <see cref="OwnerPath"/> is the owner partition (the
/// device user's home) under which the <c>_Activity</c> satellite is created.
/// </summary>
public sealed class MauiMarkdownExecutionHub
{
    public IMessageHub? Hub { get; set; }
    public string? OwnerPath { get; set; }
}

public static class MauiViewPackExtensions
{
    /// <summary>
    /// Wires the native-MAUI view pack on a hub: the layout client (streams + control type registry, no
    /// Blazor) plus the <see cref="MauiViewRegistry"/> + <see cref="IMauiControlRenderer"/>. The MAUI
    /// counterpart of <c>AddBlazor</c>.
    /// </summary>
    public static MessageHubConfiguration AddMaui(this MessageHubConfiguration config)
        => config
            .AddLayoutClient()
            .WithServices(services => services
                .AddSingleton(BuildRegistry())
                .AddSingleton<IMauiControlRenderer, MauiControlRenderer>()
                // Holder for the per-user execution sub-hub; MauiProgram populates it at startup.
                .AddSingleton<MauiMarkdownExecutionHub>());

    private static MauiViewRegistry BuildRegistry() => new MauiViewRegistry()
        // Wave 1 — leaves (containers resolve to ContainerView via the IContainerControl fallback).
        .Register<LabelControl, LabelView>()
        .Register<ButtonControl, ButtonView>()
        .Register<HtmlControl, HtmlView>()
        .Register<MarkdownControl, MarkdownView>()
        // Interactive markdown (Doc pages): renders the body + executes code blocks via the kernel.
        .Register<CollaborativeMarkdownControl, MauiCollaborativeMarkdownView>()
        .Register<IconControl, IconView>()
        .Register<ProgressControl, ProgressView>()
        .Register<NamedAreaControl, NamedAreaView>()
        // Two-way form controls.
        .Register<TextFieldControl, TextFieldView>()
        .Register<TextAreaControl, TextAreaView>()
        .Register<CheckBoxControl, CheckBoxView>()
        .Register<SwitchControl, SwitchView>()
        .Register<SelectControl, SelectView>()
        // Wave 2 — details forms: number / date / combobox / listbox.
        .Register<NumberFieldControl, NumberFieldView>()
        .Register<DateTimeControl, DateTimeView>()
        .Register<DateControl, DateView>()
        .Register<ComboboxControl, ComboboxView>()
        .Register<ListboxControl, ListboxView>()
        .Register<RadioGroupControl, RadioGroupView>()
        // Wave 2 — simple leaves: slider (range), spacer (flex gap), exception (error caption).
        .Register<SliderControl, SliderView>()
        .Register<SpacerControl, SpacerView>()
        .Register<ExceptionControl, ExceptionView>()
        // Tabular data.
        .Register<DataGridControl, DataGridView>()
        // Wave 2 — layout: a real grid + tabs (other containers fall back to ContainerView).
        .Register<LayoutGridControl, LayoutGridView>()
        .Register<TabsControl, TabsView>()
        // Wave 2 — query-driven: mesh node picker + catalog search.
        .Register<MeshNodePickerControl, MeshNodePickerView>()
        .Register<MeshSearchControl, MeshSearchView>()
        // Node-bound editor: edits a node's content directly via GetMeshNodeStream(path).Update(...).
        .Register<MeshNodeContentEditorControl, MeshNodeContentEditorView>()
        // Phase 3 — node display cards (tappable → navigate via IMauiNavigator).
        .Register<MeshNodeCardControl, MeshNodeCardView>()
        .Register<MeshNodeThumbnailControl, MeshNodeThumbnailView>()
        // Phase 3 — grouped catalog + query-driven node collection + search box.
        .Register<CatalogControl, CatalogView>()
        .Register<MeshNodeCollectionControl, MeshNodeCollectionView>()
        .Register<SearchBoxControl, SearchBoxView>()
        // Phase 3 — redirect (navigate-on-render) + code sample + dialog.
        .Register<RedirectControl, RedirectView>()
        .Register<CodeSampleControl, CodeSampleView>()
        .Register<DialogControl, DialogView>()
        // Phase 4 — charts via LiveCharts2 (OSS/MIT).
        .Register<ChartControl, ChartView>()
        // Phase 5 — native map (MAUI Map / MapKit).
        .Register<GoogleMapControl, GoogleMapView>()
        // Phase 4 — notebook cell (notebook itself is an IContainerControl → ContainerView).
        .Register<NotebookCellControl, NotebookCellView>()
        // Phase 4 — editors (markdown / code / diff) — native Editor-based.
        .Register<MarkdownEditorControl, MarkdownEditorView>()
        .Register<CodeEditorControl, CodeEditorView>()
        .Register<DiffEditorControl, DiffEditorView>()
        // Phase 3/misc — profile / appearance / item template / layout-area definition.
        .Register<UserProfileControl, UserProfileView>()
        .Register<AppearanceControl, AppearanceView>()
        .Register<ItemTemplateControl, ItemTemplateView>()
        .Register<LayoutAreaDefinitionControl, LayoutAreaDefinitionView>()
        // Phase 3 — node editors (generic name editor + role editor).
        .Register<MeshNodeEditorControl, MeshNodeEditorView>()
        .Register<MeshNodeRoleEditorControl, MeshNodeRoleEditorView>()
        // Phase 3/4 — operations (import/export/document) + file browser + pivot grid.
        .Register<NodeImportControl, NodeImportView>()
        .Register<NodeExportControl, NodeExportView>()
        .Register<ExportDocumentControl, ExportDocumentView>()
        .Register<FileBrowserControl, FileBrowserView>()
        .Register<PivotGridControl, PivotGridView>()
        // Embedded remote area (e.g. the home page's bottom chat composer) → the existing LayoutAreaView.
        .Register<LayoutAreaControl, LayoutAreaControlView>()
        // Wave 2 — nav + badges.
        .Register<BadgeControl, BadgeView>()
        .Register<NavLinkControl, NavLinkView>()
        .Register<MenuItemControl, MenuItemView>()
        // Phase 2 — agent-backed chat: the thread chat (message list + composer) + its message bubble.
        .Register<ThreadChatControl, ThreadChatView>()
        .Register<ThreadMessageBubbleControl, ThreadMessageBubbleView>();
}

// ---- Wave 1 control views -------------------------------------------------------------------------

/// <summary>Any container (Stack/LayoutGrid/Layout/Toolbar/…) → a MAUI stack of its child areas.</summary>
public sealed class ContainerView : MauiView
{
    protected override View CreateView()
    {
        // Splitter → child areas as panes in a Grid (columns for a horizontal split, rows for vertical).
        if (Control is SplitterControl splitter && Stream is not null && Control is IContainerControl sc)
            return BuildSplitter(splitter, sc);

        // Orientation: a horizontal Stack / a Toolbar (default horizontal) lays children left-to-right.
        var skin = Control is StackControl stack ? stack.Skin : null;
        var horizontal = Control switch
        {
            StackControl s => IsHorizontalOrientation(s.Skin?.Orientation, false),
            ToolbarControl t => IsHorizontalOrientation(t.Skin?.Orientation, true),
            _ => false,
        };
        var spacing = Gap(skin, horizontal) ?? 8;   // honour the skin's gap; default 8 when unset
        Microsoft.Maui.Controls.Layout layout = horizontal
            ? new HorizontalStackLayout { Spacing = spacing }
            : new VerticalStackLayout { Spacing = spacing };
        if (Stream is not null && Control is IContainerControl container)
            foreach (var named in container.Areas)
                layout.Children.Add(Renderer.RenderArea(Stream, named.Area.ToString()!));

        // Skinned wrappers (each a no-op when its skin is absent → default layout unchanged).
        if (Control is NavMenuControl nav)   // a fixed-width sidebar with a subtle panel background
        {
            layout.WidthRequest = NavWidth(nav.Skin?.Width);
            return Region(layout);
        }
        if (Control.Skins.OfType<CardSkin>().Any()) return Card(layout);
        if (Control.Skins.OfType<HeaderSkin>().Any() || Control.Skins.OfType<FooterSkin>().Any())
            return Region(layout);   // header/footer → a subtle full-width region band
        return layout;
    }

    private View BuildSplitter(SplitterControl splitter, IContainerControl container)
    {
        var horizontal = IsHorizontalOrientation(splitter.Skin?.Orientation, true);
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        var areas = container.Areas.ToList();
        for (var i = 0; i < areas.Count; i++)
            if (horizontal) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            else grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        for (var i = 0; i < areas.Count; i++)
        {
            var v = Renderer.RenderArea(Stream!, areas[i].Area.ToString()!);
            if (horizontal) grid.Add(v, i, 0); else grid.Add(v, 0, i);
        }
        return grid;
    }

    private static View Card(View content) => new Border
    {
        Padding = 12, BackgroundColor = Color.FromArgb("#2A2A2C"), StrokeThickness = 0,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
        Content = content,
    };

    // A subtle full-width region band — header / footer / nav-menu panel chrome.
    private static View Region(View content) => new Border
    {
        Padding = new Thickness(8, 6), BackgroundColor = Color.FromArgb("#1A1A1C"), StrokeThickness = 0, Content = content,
    };

    private static double NavWidth(object? width) => width switch
    {
        int i => i,
        double d => d,
        _ => double.TryParse(width?.ToString(), out var px) ? px : 250,
    };

    // Orientation rides the skin as object? (an enum at author time, a JSON string after stream round-trip).
    private static bool IsHorizontalOrientation(object? orientation, bool dflt) =>
        orientation?.ToString() is { } s ? s.Contains("Horizontal", StringComparison.OrdinalIgnoreCase) : dflt;

    // The skin's HorizontalGap/VerticalGap (an int, a double, or a CSS-ish "8px" string) → a spacing value.
    private static double? Gap(LayoutStackSkin? skin, bool horizontal)
    {
        var g = horizontal ? skin?.HorizontalGap : skin?.VerticalGap;
        if (g is null) return null;
        if (g is int i) return i;
        if (g is double d) return d;
        var s = g is JsonElement je
            ? (je.ValueKind == JsonValueKind.Number ? je.GetRawText() : je.GetString() ?? "")
            : g.ToString() ?? "";
        var digits = new string(s.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return double.TryParse(digits, System.Globalization.CultureInfo.InvariantCulture, out var px) ? px : null;
    }
}

/// <summary>A reference to a sibling area → renders that area.</summary>
public sealed class NamedAreaView : MauiView<NamedAreaControl>
{
    protected override View CreateView() =>
        Stream is not null && Model.Area is not null
            ? (View)Renderer.RenderArea(Stream, Model.Area.ToString()!)
            : new ContentView();
}

/// <summary>Text label → MAUI <see cref="Label"/>.</summary>
public sealed class LabelView : MauiView<LabelControl>
{
    private Label _label = null!;
    protected override View CreateView() => _label = new Label();
    protected override void Bind() => Bind<object>(Model.Data, v => _label.Text = v?.ToString() ?? "");
}

/// <summary>Button → MAUI <see cref="Button"/> (label only this wave; click actions next wave).</summary>
public sealed class ButtonView : MauiView<ButtonControl>
{
    private Button _button = null!;
    protected override View CreateView()
    {
        _button = new Button();
        // A click dispatches the control's server-side ClickAction via a ClickedEvent (see PostClick).
        _button.Clicked += (_, _) => PostClick();
        return _button;
    }
    protected override void Bind()
    {
        Bind<object>(Model.Data, v => _button.Text = v?.ToString() ?? "");
        Bind<bool>(Model.Disabled, d => _button.IsEnabled = !d, defaultValue: false);
    }
}

/// <summary>
/// Wraps body HTML in a minimal, dark, system-sans document for rendering in a MAUI <see cref="WebView"/>.
/// The sans-serif font stack fixes the serif-titles complaint (MAUI's HTML→<see cref="Label"/> renderer
/// fell back to a serif face for headings); the transparent background + light text match the dark app
/// shell. A plain <see cref="WebView"/> + <see cref="HtmlWebViewSource"/> (NOT a BlazorWebView) keeps the
/// maccatalyst/iOS build free of the Microsoft.AspNetCore.App shared framework.
/// </summary>
internal static class MauiHtmlDocument
{
    public static HtmlWebViewSource ForBody(string bodyHtml) => new() { Html = Wrap(bodyHtml) };

    /// <summary>
    /// Makes a content <see cref="WebView"/> grow to fit its rendered HTML (JS <c>document.body.scrollHeight</c>),
    /// re-measured on every navigation/content change. Without this a content WebView stays at its fixed
    /// <c>HeightRequest</c> and CLIPS anything taller (the space-home / Doc markdown cut-off). The outer
    /// <see cref="ScrollView"/> then scrolls the whole area. Mirrors the interactive-markdown chunk sizing.
    /// </summary>
    public static void AutoSizeToContent(WebView web)
    {
        // The rendered content height is ONLY readable via JS (document.body.scrollHeight) — MAUI exposes no
        // synchronous API for it, so this is the one inherently-async UI edge. It runs on the WebView's own
        // UI-thread Navigated event (NOT a hub action block or Blazor circuit), so it cannot deadlock the
        // actor-model scheduler the no-async rule protects. We avoid an `async void` handler (which would
        // swallow unobserved exceptions) by consuming the Task with ContinueWith + explicit fault surfacing.
        web.Navigated += (_, _) =>
            web.EvaluateJavaScriptAsync("document.body.scrollHeight").ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    // Best-effort measurement: keep the current height, but surface the failure (don't swallow).
                    System.Diagnostics.Debug.WriteLine(
                        $"[MauiHtmlDocument] WebView height measure failed: {t.Exception?.GetBaseException().Message}");
                    return;
                }
                if (double.TryParse(t.Result, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var px) && px > 0)
                    MainThread.BeginInvokeOnMainThread(() => web.HeightRequest = px + 8);
            }, TaskScheduler.Default);
    }

    // $$"""…""" raw interpolation: single { } are LITERAL CSS braces; {{bodyHtml}} is the one interpolation.
    private static string Wrap(string bodyHtml) => $$"""
        <!DOCTYPE html>
        <html><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <style>
          html,body{margin:0;padding:0;background:transparent;color:#e0e0e0;
            font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,"Helvetica Neue",Arial,sans-serif;
            font-size:15px;line-height:1.5;-webkit-text-size-adjust:100%;}
          body{padding:8px;}
          h1,h2,h3,h4,h5,h6{font-family:inherit;font-weight:600;line-height:1.25;}
          a{color:#4ea1ff;}
          code,pre{font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;}
          pre{background:rgba(255,255,255,0.06);padding:8px;border-radius:6px;overflow-x:auto;}
          table{border-collapse:collapse;}
          th,td{border:1px solid rgba(255,255,255,0.2);padding:4px 8px;}
          img{max-width:100%;height:auto;}
        </style></head>
        <body>{{bodyHtml}}</body></html>
        """;
}

/// <summary>
/// Pre-rendered HTML → a MAUI <see cref="WebView"/> (NOT a serif <see cref="Label"/>). Same dark, system-sans
/// document shell as <see cref="MarkdownView"/>, so HTML banners render with real layout + sans headings
/// instead of the Label HTML renderer's serif fallback.
/// </summary>
public sealed class HtmlView : MauiView<HtmlControl>
{
    private WebView _web = null!;
    protected override View CreateView()
    {
        _web = new WebView { HeightRequest = 40, BackgroundColor = Colors.Transparent };
        MauiHtmlDocument.AutoSizeToContent(_web);   // grow to content; never clip at a fixed height
        return _web;
    }
    protected override void Bind() =>
        Bind<object>(Model.Data, v => _web.Source = MauiHtmlDocument.ForBody(v?.ToString() ?? ""));
}

/// <summary>
/// Markdown → real HTML via the OFFICIAL MeshWeaver generator — the same Markdig pipeline the Blazor portal
/// builds (<see cref="MarkdownExtensions.CreateMarkdownPipeline"/>, including the <c>@@</c>/<c>@</c>
/// layout-area extension) — rendered in a plain MAUI <see cref="WebView"/>. The <c>@@path</c> operator emits
/// a layout-area element (via <c>LayoutAreaMarkdownRenderer</c>) rather than the literal <c>@@path</c> text,
/// markdown is formatted (links/tables/emoji), and headings render sans (fixing the serif-titles complaint).
/// NOT a BlazorWebView → no Microsoft.AspNetCore.App in the maccatalyst build. Markdown→HTML is a pure
/// synchronous call; the WebView mutation runs on the UI thread (Bind marshals via MainThread).
/// </summary>
public sealed class MarkdownView : MauiView<MarkdownControl>
{
    private WebView _web = null!;
    private string? _nodePath;

    protected override View CreateView()
    {
        // Relative @@ embeds resolve against the authoring node's path (mirrors MarkdownView.razor.cs).
        _nodePath = Model.NodePath;
        _web = new WebView { HeightRequest = 40, BackgroundColor = Colors.Transparent };
        // Grow to fit the rendered markdown — a fixed 240px clipped the space-home / Doc content
        // ("cut off at Bring in existing files"). The node area's outer ScrollView scrolls the whole page.
        MauiHtmlDocument.AutoSizeToContent(_web);
        return _web;
    }

    protected override void Bind() =>
        Bind<object>(Model.Markdown, v => _web.Source = Render(v?.ToString() ?? ""));

    private HtmlWebViewSource Render(string markdown)
    {
        // The official cached, immutable pipeline. collection is null (no static-asset href rewriting on the
        // native client); currentNodePath threads the relative-@@-embed base path. Markdig.Markdown.ToHtml is
        // the same call MarkdownViewLogic.Render makes server-side.
        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(collection: null, currentNodePath: _nodePath);
        var html = Markdig.Markdown.ToHtml(markdown, pipeline);
        return MauiHtmlDocument.ForBody(html);
    }
}

/// <summary>
/// Interactive markdown — the Overview area of Markdown/Doc nodes emits a
/// <see cref="CollaborativeMarkdownControl"/>. Renders the body natively (the same dark/sans WebView shell
/// as <see cref="MarkdownView"/>) AND, on JIT platforms, EXECUTES every code block: it creates a per-view
/// Activity from the REAL per-user sub-hub (<see cref="MauiMarkdownExecutionHub"/> — never the top-level
/// mesh hub) via the framework's <see cref="MarkdownViewLogic.CreateActivityAndSubmit"/>, and embeds each
/// kernel result area as a native <see cref="LayoutAreaView"/> over the activity address. Kernel/compile
/// failures surface through that embedded area (the kernel writes a <c>**Execution failed**</c> control +
/// flips the activity log to Failed); a create/route failure paints a red notice; on iOS (no JIT) the body
/// renders read-only with an "execution unavailable" notice — never a silent spinner. Emancipated from
/// Blazor: reuses <see cref="MarkdownViewLogic"/>; no <c>Microsoft.AspNetCore.App</c>.
/// <para>Collaborative overlays (inline comments, tracked-changes accept/reject, view-mode switch) are a
/// separate Blazor-only feature and are intentionally not reproduced here — Doc pages are read-only.</para>
/// </summary>
public sealed class MauiCollaborativeMarkdownView : MauiView<CollaborativeMarkdownControl>
{
    private VerticalStackLayout _root = null!;
    private bool _submitted;

    protected override View CreateView() => _root = new VerticalStackLayout { Spacing = 8 };

    protected override void Bind() =>
        Bind<object>(Model.Value, v => Rebuild(MarkdownViewLogic.CoerceString(v) ?? ""));

    private void Rebuild(string markdown)
    {
        _root.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown)) return;

        // Same framework pipeline the Blazor views use: HTML (kernel placeholders intact) + typed submissions.
        var result = MarkdownViewLogic.Render(markdown, collection: null, currentNodePath: Model.NodePath);

        // No executable blocks (the common + all read-only case) → one WebView for the whole doc.
        if (result.CodeSubmissions is not { Count: > 0 })
        {
            _root.Children.Add(NewHtmlChunk(result.Html));
            AddCommentsPanel();
            AddTrackedChangesPanel();
            return;
        }

        // Real per-user sub-hub to start the activity FROM (never the top-level mesh hub).
        var exec = Stream?.Hub.ServiceProvider.GetService<MauiMarkdownExecutionHub>();
        var senderHub = exec?.Hub;
        var ownerPath = exec?.OwnerPath;

        // Segment the HTML into ordered (html | kernel-area) parts; build the stack with placeholders.
        var pending = new List<(ContentView Host, string SubmissionId)>();
        foreach (var (html, submissionId) in MarkdownViewLogic.SplitKernelResultAreas(result.Html))
        {
            if (submissionId is not null)
            {
                var host = new ContentView { Content = Notice("Starting interactive kernel…", Colors.Gray) };
                pending.Add((host, submissionId));
                _root.Children.Add(host);
            }
            else if (!string.IsNullOrEmpty(html))
                _root.Children.Add(NewHtmlChunk(html!));
        }

        AddCommentsPanel();
        AddTrackedChangesPanel();

        if (_submitted) return;
        _submitted = true;

        // Real iOS/tvOS devices have no JIT → Roslyn can't run; surface that instead of a spinner.
        // 🚨 Mac Catalyst reports OperatingSystem.IsIOS()==true (it's an iOS-derived TFM) but DOES have
        // JIT — it's a full desktop macOS process — so it must be EXECUTED, not gated out. Exclude it.
        if (OperatingSystem.IsIOS() && !OperatingSystem.IsMacCatalyst())
        {
            SetAll(pending, "Interactive code execution is unavailable on this device (no JIT).", Colors.Gray);
            return;
        }
        if (senderHub is null || string.IsNullOrEmpty(ownerPath))
        {
            SetAll(pending, "Interactive code execution unavailable — no owning hub.", Colors.OrangeRed);
            return;
        }

        var meshService = senderHub.ServiceProvider.GetRequiredService<IMeshService>();
        var kernelId = Guid.NewGuid().ToString("N");
        var activityAddress = new Address($"{ownerPath}/_Activity/markdown-{kernelId}");

        MarkdownViewLogic.CreateActivityAndSubmit(
            senderHub, meshService, activityAddress, ownerPath, kernelId, result.CodeSubmissions!,
            // Activity is routable → embed the live kernel result areas (subscribe-after-create).
            onReady: () => MainThread.BeginInvokeOnMainThread(() =>
            {
                var workspace = Stream!.Hub.GetWorkspace();
                foreach (var (host, submissionId) in pending)
                    host.Content = new LayoutAreaView(
                        workspace, activityAddress, new LayoutAreaReference(submissionId), Renderer);
            }),
            // Create/route failed → surface it (no silent spinner).
            onError: ex => MainThread.BeginInvokeOnMainThread(() =>
                SetAll(pending, $"⚠ Could not start interactive kernel — {ex.GetType().Name}: {ex.Message}",
                    Colors.OrangeRed)));
    }

    // Comments panel: a LIVE list of the node's _Comment satellites (author + quoted text + body) plus, when
    // CanComment, an add box that creates a comment via the canonical CreateNode — the live query then
    // re-renders. Anchoring a comment to a text selection (HighlightedText) is a refinement.
    private void AddCommentsPanel()
    {
        if (Stream is null || string.IsNullOrEmpty(Model.NodePath)) return;
        var list = new VerticalStackLayout { Spacing = 6 };
        var panel = new VerticalStackLayout { Spacing = 6, Margin = new Thickness(0, 12, 0, 0), Children = { list } };

        if (Model.CanComment)
        {
            var input = new Entry { Placeholder = "Add a comment…", FontSize = 12, TextColor = Colors.White };
            var add = new Button { Text = "Comment", FontSize = 11, BackgroundColor = Colors.RoyalBlue, TextColor = Colors.White, CornerRadius = 6, Padding = new Thickness(10, 4) };
            add.Clicked += (_, _) => AddComment(input);   // AddComment clears the input only on a successful create
            var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
            row.Add(input, 0, 0);
            row.Add(add, 1, 0);
            panel.Children.Add(row);
        }

        _root.Children.Add(panel);
        var sub = Stream.Hub.GetQuery("comments:" + Model.NodePath, $"namespace:{Model.NodePath}/_Comment nodeType:Comment")
            .Subscribe(nodes => MainThread.BeginInvokeOnMainThread(() => RenderComments(list, nodes)));
        Disposables.Add(sub);
    }

    private void RenderComments(VerticalStackLayout list, IEnumerable<MeshNode> nodes)
    {
        if (Stream is null) return;
        list.Children.Clear();
        var opts = Stream.Hub.JsonSerializerOptions;
        // Show only Active comments; Resolve flips Status → they drop off (live query re-renders).
        var comments = nodes
            .Select(n => (Node: n, Comment: n.ContentAs<MeshWeaver.Mesh.Comment>(opts)))
            .Where(t => t.Comment is { Status: MeshWeaver.Mesh.CommentStatus.Active })
            .ToList();
        if (comments.Count == 0) return;
        list.Children.Add(new Label { Text = $"Comments ({comments.Count})", FontAttributes = FontAttributes.Bold, FontSize = 13, TextColor = Colors.White });
        foreach (var (node, c) in comments)
        {
            var box = new VerticalStackLayout { Spacing = 2 };
            box.Children.Add(new Label { Text = string.IsNullOrWhiteSpace(c!.Author) ? "Someone" : c.Author, FontSize = 11, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#C0C0C0") });
            if (!string.IsNullOrWhiteSpace(c.HighlightedText))
                box.Children.Add(new Label { Text = "“" + c.HighlightedText + "”", FontSize = 11, FontAttributes = FontAttributes.Italic, TextColor = Color.FromArgb("#9A9A9A") });
            box.Children.Add(new Label { Text = c.Text, FontSize = 12, TextColor = Colors.White, LineBreakMode = LineBreakMode.WordWrap });

            var path = node.Path;
            var resolve = new Button { Text = "Resolve", FontSize = 11, BackgroundColor = Color.FromArgb("#3A3A3C"), TextColor = Colors.White, CornerRadius = 6, Padding = new Thickness(10, 4) };
            resolve.Clicked += (_, _) => ResolveComment(path);
            box.Children.Add(new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, Children = { resolve } });

            list.Children.Add(new Border
            {
                Padding = 8, BackgroundColor = Color.FromArgb("#2A2A2C"), StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 }, Content = box,
            });
        }
    }

    // Surfaces a collaborative write failure (RLS/routing/access) instead of swallowing it — mirrors the
    // LogWarning-on-update-failure convention the other views use.
    private void Log(Exception ex, string what) =>
        Stream?.Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Maui.Collaborative")
            .LogWarning(ex, "Collaborative {What} failed for {Path}", what, Model.NodePath);

    // Resolve a comment: flip its Content.Status → Resolved via the canonical external-node stream update.
    private void ResolveComment(string path)
    {
        if (Stream is null) return;
        var opts = Stream.Hub.JsonSerializerOptions;
        Stream.Hub.GetMeshNodeStream(path).Update(node =>
        {
            var c = node.ContentAs<MeshWeaver.Mesh.Comment>(opts);
            return c is null ? node : node with { Content = c with { Status = MeshWeaver.Mesh.CommentStatus.Resolved } };
        }).Subscribe(_ => { }, ex => Log(ex, "resolve comment"));
    }

    // Creates a _Comment satellite via the canonical CreateNode (carries the caller's AccessContext). Clears
    // the input only on success (so a denied/failed create keeps the user's text); logs the failure.
    private void AddComment(Entry input)
    {
        var text = input.Text;
        if (Stream is null || string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(Model.NodePath)) return;
        var meshService = Stream.Hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null) return;
        var author = Stream.Hub.ServiceProvider.GetService<AccessService>()?.Context?.Name ?? "You";
        var id = Guid.NewGuid().ToString("N")[..8];
        var content = new MeshWeaver.Mesh.Comment { Text = text!.Trim(), Author = author, PrimaryNodePath = Model.NodePath };
        var node = new MeshNode(id, $"{Model.NodePath}/_Comment") { NodeType = "Comment", Content = content };
        meshService.CreateNode(node).Subscribe(
            _ => MainThread.BeginInvokeOnMainThread(() => input.Text = ""),   // clear on success → keep text on failure
            ex => Log(ex, "add comment"));                                    // live query re-renders the list
    }

    // Pending tracked-changes panel: lists the node's _Tracking satellites (author + original→new) with a
    // functional Reject (delete the satellite). Accept (apply NewText to the collaborative doc) is a refinement.
    private void AddTrackedChangesPanel()
    {
        if (Stream is null || string.IsNullOrEmpty(Model.NodePath)) return;
        var panel = new VerticalStackLayout { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        _root.Children.Add(panel);
        // Live: re-render on every query emission (accept/reject of a satellite re-fires this) — no Take(1),
        // which would freeze the panel after the first snapshot.
        var sub = Stream.Hub.GetQuery("changes:" + Model.NodePath, $"namespace:{Model.NodePath}/_Tracking nodeType:TrackedChange")
            .Subscribe(nodes => MainThread.BeginInvokeOnMainThread(() => RenderTrackedChanges(panel, nodes)));
        Disposables.Add(sub);
    }

    private void RenderTrackedChanges(VerticalStackLayout panel, IEnumerable<MeshNode> nodes)
    {
        if (Stream is null) return;
        panel.Children.Clear();   // rebuild from the current snapshot — never append to a stale list
        var opts = Stream.Hub.JsonSerializerOptions;
        var pending = nodes
            .Select(n => (Node: n, Change: n.ContentAs<MeshWeaver.Mesh.TrackedChange>(opts)))
            .Where(t => t.Change is { Status: MeshWeaver.Mesh.TrackedChangeStatus.Pending })
            .ToList();
        if (pending.Count == 0) return;
        panel.Children.Add(new Label { Text = $"Suggested changes ({pending.Count})", FontAttributes = FontAttributes.Bold, FontSize = 13, TextColor = Colors.White });
        foreach (var (node, change) in pending)
        {
            var box = new VerticalStackLayout { Spacing = 2 };
            box.Children.Add(new Label { Text = string.IsNullOrWhiteSpace(change!.Author) ? "Someone" : change.Author, FontSize = 11, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#C0C0C0") });
            if (!string.IsNullOrWhiteSpace(change.OriginalText))
                box.Children.Add(new Label { Text = "− " + change.OriginalText, FontSize = 11, TextColor = Color.FromArgb("#E06C75") });
            if (!string.IsNullOrWhiteSpace(change.NewText))
                box.Children.Add(new Label { Text = "+ " + change.NewText, FontSize = 11, TextColor = Color.FromArgb("#98C379") });

            var path = node.Path;
            var ch = change!;
            var accept = new Button { Text = "Accept", FontSize = 11, BackgroundColor = Color.FromArgb("#2E7D32"), TextColor = Colors.White, CornerRadius = 6, Padding = new Thickness(10, 4) };
            accept.Clicked += (_, _) => AcceptChange(ch, path);
            var reject = new Button { Text = "Reject", FontSize = 11, BackgroundColor = Color.FromArgb("#3A3A3C"), TextColor = Colors.White, CornerRadius = 6, Padding = new Thickness(10, 4) };
            reject.Clicked += (_, _) => Stream?.Hub.ServiceProvider.GetService<IMeshService>()?.DeleteNode(path)
                .Subscribe(_ => { }, ex => Log(ex, "reject change"));
            box.Children.Add(new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, Children = { accept, reject } });

            panel.Children.Add(new Border
            {
                Padding = 8, BackgroundColor = Color.FromArgb("#2A2A2C"), StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 }, Content = box,
            });
        }
    }

    // Accept a tracked change: apply its suggested text into the doc, then drop the satellite — mirrors the
    // Blazor accept (StripAllMarkers → ResolveEffective(version) → Apply → write MarkdownContent.Content).
    private void AcceptChange(MeshWeaver.Mesh.TrackedChange change, string satellitePath)
    {
        if (Stream is null || string.IsNullOrEmpty(Model.NodePath)) return;
        var opts = Stream.Hub.JsonSerializerOptions;
        var meshService = Stream.Hub.ServiceProvider.GetService<IMeshService>();
        var read = Stream.Hub.GetMeshNodeStream(Model.NodePath).Where(n => n is not null).Take(1)
            .Timeout(TimeSpan.FromSeconds(10))   // one-shot read for the action — never leak the subscription
            .Subscribe(node => MainThread.BeginInvokeOnMainThread(() =>
            {
                var md = node!.ContentAs<MeshWeaver.Markdown.MarkdownContent>(opts);
                if (md is null) return;
                var clean = MeshWeaver.Markdown.Collaboration.MarkdownAnnotationParser.StripAllMarkers(md.Content);
                var resolved = ChangeRendering.ResolveEffective(change, clean, node.Version);
                var newClean = ChangeRendering.Apply(clean, resolved);
                Stream.Hub.GetMeshNodeStream(Model.NodePath)
                    .Update(n => n.Content is MeshWeaver.Markdown.MarkdownContent m
                        ? n with { Content = m with { Content = newClean } } : n)
                    .Subscribe(
                        _ => meshService?.DeleteNode(satellitePath).Subscribe(__ => { }, ex => Log(ex, "drop accepted change")),
                        ex => Log(ex, "apply accepted change"));
            }), ex => Log(ex, "read doc for accept"));
        Disposables.Add(read);
    }

    // A WebView chunk in MarkdownView's dark/sans shell, auto-sized to its content (JS scrollHeight).
    private static View NewHtmlChunk(string bodyHtml)
    {
        var web = new WebView { HeightRequest = 40, BackgroundColor = Colors.Transparent };
        MauiHtmlDocument.AutoSizeToContent(web);
        web.Source = MauiHtmlDocument.ForBody(bodyHtml);
        return web;
    }

    private static void SetAll(List<(ContentView Host, string SubmissionId)> pending, string text, Color color)
    {
        foreach (var (host, _) in pending) host.Content = Notice(text, color);
    }

    private static View Notice(string text, Color color) => new Label
    {
        Text = text, FontSize = 12, TextColor = color,
        Margin = new Thickness(8), LineBreakMode = LineBreakMode.WordWrap,
    };
}

/// <summary>Badge → a pill-styled <see cref="Label"/> in a <see cref="Border"/>.</summary>
public sealed class BadgeView : MauiView<BadgeControl>
{
    private Label _label = null!;
    protected override View CreateView()
    {
        _label = new Label { Padding = new Thickness(8, 2), TextColor = Colors.White };
        return new Border
        {
            BackgroundColor = Colors.SlateGray,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Content = _label,
            HorizontalOptions = LayoutOptions.Start,
        };
    }
    protected override void Bind() => Bind<object>(Model.Data, v => _label.Text = v?.ToString() ?? "");
}

/// <summary>Navigation link → a MAUI <see cref="Button"/>. A link with a <c>Url</c> navigates the shell via
/// <see cref="IMauiNavigator"/> (the href path); links without a Url fall back to their server
/// <c>ClickAction</c> (a <see cref="MauiView.PostClick"/> <c>ClickedEvent</c>).</summary>
public sealed class NavLinkView : MauiView<NavLinkControl>
{
    private Button _button = null!;
    private string? _href;
    protected override View CreateView()
    {
        _button = new Button { HorizontalOptions = LayoutOptions.Start };
        _button.Clicked += (_, _) =>
        {
            var nav = !string.IsNullOrWhiteSpace(_href)
                ? Stream?.Hub.ServiceProvider.GetService<IMauiNavigator>()
                : null;
            if (nav is not null) nav.NavigateTo(_href!, _button.Text);
            else PostClick(); // ClickAction-based link (or no navigator registered).
        };
        return _button;
    }
    protected override void Bind()
    {
        Bind<object>(Model.Title, v => _button.Text = v?.ToString() ?? "");
        Bind<object>(Model.Url, v => _href = v?.ToString());
    }
}

/// <summary>Menu item → a MAUI <see cref="Label"/> (title).</summary>
public sealed class MenuItemView : MauiView<MenuItemControl>
{
    private Label _label = null!;
    protected override View CreateView()
    {
        _label = new Label();
        // Menu items act via their ClickAction — make the label tappable and dispatch it.
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => PostClick();
        _label.GestureRecognizers.Add(tap);
        return _label;
    }
    protected override void Bind() => Bind<object>(Model.Title, v => _label.Text = v?.ToString() ?? "");
}

/// <summary>
/// Icon → native MAUI. An icon value can be raw <c>&lt;svg&gt;</c> markup, an image URL / data-URI, or a
/// glyph/emoji/name. A native <see cref="Label"/> (the old impl) can only show text, so SVG markup never
/// appeared. Now: SVG markup (or an <c>.svg</c>/<c>data:image/svg</c> source) renders in a tiny transparent
/// <see cref="WebView"/> (the same not-a-BlazorWebView approach the markdown views use — keeps the build
/// AspNetCore-free); raster URLs/data-URIs use a native <see cref="Image"/>; everything else stays a Label.
/// SVGs using <c>currentColor</c> inherit the dark shell's light foreground so they're visible.
/// </summary>
public sealed class IconView : MauiView<IconControl>
{
    private const int Size = 20;
    private readonly ContentView _host = new();
    protected override View CreateView() => _host;
    protected override void Bind() =>
        Bind<object>(Model.Data, v => _host.Content = MauiImagery.ForSource(MarkdownViewLogic.CoerceString(v), Size));
}

/// <summary>
/// Renders an icon/image value — raw <c>&lt;svg&gt;</c> markup, an image URL / data-URI, or a glyph /
/// emoji / initials — as a native view sized to <c>size</c>: SVG markup (or an <c>.svg</c>/<c>data:image/svg</c>
/// source) in a tiny transparent <see cref="WebView"/> (not-a-BlazorWebView → AspNetCore-free), raster
/// URLs/data-URIs in a native <see cref="Image"/>, everything else a <see cref="Label"/>. Shared by the icon
/// + node-card/thumbnail views. SVGs using <c>currentColor</c> inherit the dark shell's light foreground.
/// </summary>
internal static class MauiImagery
{
    public static View ForSource(string? value, double size)
    {
        var icon = (value ?? "").Trim();
        if (string.IsNullOrEmpty(icon)) return new ContentView { WidthRequest = size, HeightRequest = size };

        var isSvg = icon.Contains("<svg", StringComparison.OrdinalIgnoreCase)
                    || icon.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase)
                    || (icon.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        && icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase));
        if (isSvg)
            return new WebView
            {
                WidthRequest = size, HeightRequest = size, BackgroundColor = Colors.Transparent,
                Source = new HtmlWebViewSource { Html = SvgHtml(icon, (int)size) },
            };

        // Raster image (png/jpg/…) by URL or data-URI → native Image.
        if (icon.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || icon.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || icon.StartsWith("/", StringComparison.Ordinal))
            return new Image { Source = icon, WidthRequest = size, HeightRequest = size, Aspect = Aspect.AspectFit };

        // Glyph / emoji / initials — no native icon set, so show as centred text.
        return new Label
        {
            Text = icon, FontSize = size * 0.8, TextColor = Colors.White,
            HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center,
        };
    }

    // $$"""…""" raw interpolation: single { } are LITERAL CSS braces; {{…}} are interpolations.
    private static string SvgHtml(string icon, int px)
    {
        var body = icon.Contains("<svg", StringComparison.OrdinalIgnoreCase)
            ? icon
            : $"<img src=\"{icon}\" style=\"width:{px}px;height:{px}px\"/>";
        return $$"""
            <!DOCTYPE html><html><head><meta charset="utf-8">
            <style>html,body{margin:0;padding:0;background:transparent;overflow:hidden;color:#e0e0e0}
            svg{width:{{px}}px;height:{{px}}px;display:block;fill:currentColor}
            img{display:block}</style></head><body>{{body}}</body></html>
            """;
    }
}

/// <summary>
/// A mesh-node card → a tappable native card (image/emoji + title + truncated description) that navigates
/// to the node via <see cref="IMauiNavigator"/> — the AspNetCore-free counterpart of Blazor's
/// MeshNodeCardView. When <see cref="MeshNodeCardControl.ItemArea"/> is set, the card body renders that
/// area instead (custom card content).
/// </summary>
public sealed class MeshNodeCardView : MauiView<MeshNodeCardControl>
{
    protected override View CreateView()
    {
        View body;
        if (!string.IsNullOrEmpty(Model.ItemArea) && Stream is not null)
            body = Renderer.RenderArea(Stream, Model.ItemArea!);
        else
        {
            var stack = new VerticalStackLayout { Spacing = 6 };
            if (!string.IsNullOrWhiteSpace(Model.ImageUrl))
                stack.Children.Add(MauiImagery.ForSource(Model.ImageUrl, 48));
            stack.Children.Add(new Label { Text = Model.Title ?? "", FontAttributes = FontAttributes.Bold, TextColor = Colors.White });
            if (!string.IsNullOrWhiteSpace(Model.Description))
                stack.Children.Add(new Label
                {
                    Text = Model.Description, FontSize = 12, TextColor = Color.FromArgb("#C0C0C0"),
                    MaxLines = 3, LineBreakMode = LineBreakMode.TailTruncation,
                });
            body = stack;
        }
        return MakeCard(body, AsBool(Model.DisableNavigation, false) ? null : Model.NodePath, Model.Title);
    }

    // A rounded dark card; tappable → navigate to nodePath (when non-null) via IMauiNavigator.
    internal View MakeCard(View content, string? nodePath, string? title)
    {
        var border = new Border
        {
            Padding = 12,
            BackgroundColor = Color.FromArgb("#2A2A2C"),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Content = content,
        };
        if (!string.IsNullOrEmpty(nodePath))
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => Stream?.Hub.ServiceProvider.GetService<IMauiNavigator>()?.NavigateTo(nodePath, title);
            border.GestureRecognizers.Add(tap);
        }
        return border;
    }

    private static bool AsBool(object? v, bool dflt) => v switch
    {
        null => dflt,
        bool b => b,
        JsonElement je when je.ValueKind == JsonValueKind.True => true,
        JsonElement je when je.ValueKind == JsonValueKind.False => false,
        _ => bool.TryParse(v.ToString(), out var p) ? p : dflt,
    };
}

/// <summary>
/// A mesh-node thumbnail → a tappable horizontal card: 48px avatar (image/emoji/initials) + title +
/// 2-line description, navigating to the node. Native counterpart of Blazor's MeshNodeThumbnailView.
/// </summary>
public sealed class MeshNodeThumbnailView : MauiView<MeshNodeThumbnailControl>
{
    protected override View CreateView()
    {
        var avatar = MauiImagery.ForSource(
            string.IsNullOrWhiteSpace(Model.ImageUrl) ? Initials(Model.Title) : Model.ImageUrl, 48);
        avatar.WidthRequest = 48; avatar.HeightRequest = 48;

        var texts = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        texts.Children.Add(new Label { Text = Model.Title ?? "", FontAttributes = FontAttributes.Bold, TextColor = Colors.White });
        if (!string.IsNullOrWhiteSpace(Model.Description))
            texts.Children.Add(new Label
            {
                Text = Model.Description, FontSize = 12, TextColor = Color.FromArgb("#C0C0C0"),
                MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation,
            });
        if (!string.IsNullOrWhiteSpace(Model.NodeType))
            texts.Children.Add(new Label { Text = Model.NodeType, FontSize = 10, TextColor = Color.FromArgb("#9A9A9A") });

        var border = new Border
        {
            Padding = 10, MinimumWidthRequest = 320,
            BackgroundColor = Color.FromArgb("#2A2A2C"),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Content = new HorizontalStackLayout { Spacing = 12, Children = { avatar, texts } },
        };
        if (!string.IsNullOrEmpty(Model.NodePath))
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => Stream?.Hub.ServiceProvider.GetService<IMauiNavigator>()?.NavigateTo(Model.NodePath, Model.Title);
            border.GestureRecognizers.Add(tap);
        }
        return border;
    }

    // Up to two leading letters of the title — the placeholder avatar when no image is set.
    private static string Initials(string? title)
    {
        var parts = (title ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        return parts.Length == 1
            ? parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant()
            : (parts[0][..1] + parts[1][..1]).ToUpperInvariant();
    }
}

/// <summary>
/// A grouped catalog → native: each <see cref="CatalogGroup"/> renders a (tappable, collapsible) section
/// header (emoji + label + count) over a wrapping grid of the group's item controls (typically
/// <see cref="MeshNodeCardControl"/>, rendered via the registry). The AspNetCore-free counterpart of
/// Blazor's catalog grid; sections collapse by toggling the grid's visibility (no extra dependency).
/// </summary>
public sealed class CatalogView : MauiView<CatalogControl>
{
    protected override View CreateView()
    {
        var root = new VerticalStackLayout { Spacing = Model.SectionGap };
        foreach (var group in Model.Groups.OrderBy(g => g.Order))
        {
            var count = group.TotalCount > 0 ? group.TotalCount : group.Items.Count;
            var headerText = (string.IsNullOrEmpty(group.Emoji) ? "" : group.Emoji + "  ") + group.Label
                + (Model.ShowCounts ? $"   ({count})" : "");
            var header = new Label { Text = headerText, FontAttributes = FontAttributes.Bold, FontSize = 16, TextColor = Colors.White };

            // Fully-qualified: MeshWeaver.Layout also defines FlexWrap/FlexDirection (the framework skin enums).
            var grid = new FlexLayout
            {
                Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
                Direction = Microsoft.Maui.Layouts.FlexDirection.Row,
            };
            if (Stream is not null)
                foreach (var item in group.Items)
                {
                    // Each item control (e.g. a node card) gets a fixed tile so the row wraps into columns.
                    var tile = new ContentView
                    {
                        WidthRequest = 280, HeightRequest = Model.CardHeight,
                        Margin = new Thickness(Model.Spacing * 2),
                        Content = Renderer.RenderControl(item, Stream, Area),
                    };
                    grid.Children.Add(tile);
                }

            if (Model.CollapsibleSections)
            {
                grid.IsVisible = group.IsExpanded;
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => grid.IsVisible = !grid.IsVisible;
                header.GestureRecognizers.Add(tap);
            }
            root.Children.Add(new VerticalStackLayout { Spacing = 8, Children = { header, grid } });
        }
        return root;
    }
}

/// <summary>
/// A query-driven mesh-node collection → a live list of node rows (name + type), each tappable to navigate
/// via <see cref="IMauiNavigator"/>, with an optional per-row delete (when <c>Deletable</c>). Reuses the
/// shared synced query (<c>hub.GetQuery</c>) — the same path the picker/search views use; the list refreshes
/// as the query results change. (The add-dialog from <c>ShowAdd</c> is a later wave.)
/// </summary>
public sealed class MeshNodeCollectionView : MauiView<MeshNodeCollectionControl>
{
    private VerticalStackLayout _root = null!;
    protected override View CreateView() => _root = new VerticalStackLayout { Spacing = 4 };

    protected override void Bind()
    {
        if (Stream is null) return;
        var queries = Model.Queries is { Length: > 0 } q ? q : new[] { "" };
        var sub = Stream.Hub.GetQuery("collection:" + string.Join("|", queries), queries)
            .Subscribe(nodes => MainThread.BeginInvokeOnMainThread(() => Render(nodes)));
        Disposables.Add(sub);
    }

    private void Render(IEnumerable<MeshNode> nodes)
    {
        _root.Children.Clear();
        var deletable = Model.Deletable;
        foreach (var node in nodes.DistinctBy(n => n.Path))
        {
            var n = node;
            var name = new Label
            {
                Text = n.Name ?? n.Path, TextColor = Colors.White,
                VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.StartAndExpand,
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => Stream?.Hub.ServiceProvider.GetService<IMauiNavigator>()?.NavigateTo(n.Path, n.Name);
            name.GestureRecognizers.Add(tap);

            var row = new HorizontalStackLayout { Spacing = 8, Children = { name } };
            if (!string.IsNullOrWhiteSpace(n.NodeType))
                row.Children.Add(new Label { Text = n.NodeType, FontSize = 10, TextColor = Color.FromArgb("#9A9A9A"), VerticalOptions = LayoutOptions.Center });
            if (deletable)
            {
                var del = new Button { Text = "🗑", BackgroundColor = Colors.Transparent, Padding = 0 };
                del.Clicked += (_, _) => Stream?.Hub.ServiceProvider.GetService<IMeshService>()?.DeleteNode(n.Path)
                    .Subscribe(_ => { }, _ => { });
                row.Children.Add(del);
            }
            _root.Children.Add(row);
        }
    }
}

/// <summary>
/// A search box → a native <see cref="Entry"/> + Search button that runs the (namespace-scoped) query via
/// the shared synced query (<c>hub.GetQuery</c>) and lists tappable results that navigate via
/// <see cref="IMauiNavigator"/>. The AspNetCore-free counterpart of Blazor's SearchBox (its Monaco
/// <c>@</c>-autocomplete is a later wave); enough to make a space's embedded "Search" area work natively.
/// </summary>
public sealed class SearchBoxView : MauiView<SearchBoxControl>
{
    private Entry _entry = null!;
    private VerticalStackLayout _results = null!;

    protected override View CreateView()
    {
        _entry = new Entry
        {
            Placeholder = MarkdownViewLogic.CoerceString(Model.Placeholder) ?? "Search…",
            ReturnType = ReturnType.Search, TextColor = Colors.White,
        };
        _entry.Completed += (_, _) => RunSearch(_entry.Text);
        var button = new Button { Text = "Search", BackgroundColor = Colors.RoyalBlue, TextColor = Colors.White, CornerRadius = 8 };
        button.Clicked += (_, _) => RunSearch(_entry.Text);

        var bar = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        bar.Add(_entry, 0, 0);
        bar.Add(button, 1, 0);

        _results = new VerticalStackLayout { Spacing = 2 };
        return new VerticalStackLayout { Spacing = 8, Children = { bar, _results } };
    }

    protected override void Bind()
    {
        var preset = MarkdownViewLogic.CoerceString(Model.Value);
        if (!string.IsNullOrWhiteSpace(preset)) _entry.Text = preset;
    }

    private void RunSearch(string? text)
    {
        if (Stream is null || string.IsNullOrWhiteSpace(text)) return;
        var ns = MarkdownViewLogic.CoerceString(Model.Namespace);
        var query = string.IsNullOrWhiteSpace(ns) ? text!.Trim() : $"namespace:{ns} {text}".Trim();
        var sub = Stream.Hub.GetQuery("searchbox:" + query, query).Take(1)
            .Subscribe(nodes => MainThread.BeginInvokeOnMainThread(() => RenderResults(nodes)));
        Disposables.Add(sub);
    }

    private void RenderResults(IEnumerable<MeshNode> nodes)
    {
        _results.Children.Clear();
        var max = int.TryParse(MarkdownViewLogic.CoerceString(Model.MaxSuggestions), out var m) && m > 0 ? m : 20;
        foreach (var node in nodes.DistinctBy(n => n.Path).Take(max))
        {
            var n = node;
            var lbl = new Label { Text = n.Name ?? n.Path, TextColor = Color.FromArgb("#4ea1ff"), Padding = new Thickness(4, 2) };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => Stream?.Hub.ServiceProvider.GetService<IMauiNavigator>()?.NavigateTo(n.Path, n.Name);
            lbl.GestureRecognizers.Add(tap);
            _results.Children.Add(lbl);
        }
    }
}

/// <summary>A redirect → navigates the shell to <see cref="RedirectControl.Href"/> on render (via
/// <see cref="IMauiNavigator"/>); renders nothing itself. The native counterpart of the portal's redirect.</summary>
public sealed class RedirectView : MauiView<RedirectControl>
{
    protected override View CreateView() => new ContentView();
    protected override void Bind()
    {
        var href = MarkdownViewLogic.CoerceString(Model.Href);
        if (!string.IsNullOrWhiteSpace(href) && Stream is not null)
            MainThread.BeginInvokeOnMainThread(() =>
                Stream.Hub.ServiceProvider.GetService<IMauiNavigator>()?.NavigateTo(href!));
    }
}

/// <summary>A code sample → a monospace, horizontally-scrollable code block on a dark panel.</summary>
public sealed class CodeSampleView : MauiView<CodeSampleControl>
{
    private Label _code = null!;
    protected override View CreateView()
    {
        _code = new Label { FontFamily = "Menlo", FontSize = 13, TextColor = Color.FromArgb("#E0E0E0") };
        return new Border
        {
            Padding = 10, BackgroundColor = Color.FromArgb("#1E1E1E"), StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Content = new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = _code },
        };
    }
    protected override void Bind() => Bind<object>(Model.Data, v => _code.Text = MarkdownViewLogic.CoerceString(v) ?? "");
}

/// <summary>
/// A dialog → a card-styled panel with a title, the <see cref="DialogControl.ContentArea"/> rendered as a
/// child area, and (when it has actions) the <see cref="DialogControl.ActionsArea"/> below. Rendered inline
/// rather than as an OS modal (no Page context here) — the AspNetCore-free counterpart of Blazor's dialog.
/// </summary>
public sealed class DialogView : MauiView<DialogControl>
{
    protected override View CreateView()
    {
        var stack = new VerticalStackLayout { Spacing = 10 };
        stack.Children.Add(new Label
        {
            Text = MarkdownViewLogic.CoerceString(Model.Title) ?? "Dialog",
            FontAttributes = FontAttributes.Bold, FontSize = 16, TextColor = Colors.White,
        });
        if (Stream is not null && Model.ContentArea?.Area is not null)
            stack.Children.Add(Renderer.RenderArea(Stream, Model.ContentArea.Area.ToString()!));
        if (Stream is not null && Model.HasActions && Model.ActionsArea?.Area is not null)
            stack.Children.Add(Renderer.RenderArea(Stream, Model.ActionsArea.Area.ToString()!));
        return new Border
        {
            Padding = 16,
            BackgroundColor = Color.FromArgb("#2A2A2C"),
            Stroke = Color.FromArgb("#444"), StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Content = stack,
        };
    }
}

/// <summary>
/// A chart → native via LiveCharts2 (MIT, SkiaSharp): maps the framework <see cref="ChartControl"/>'s
/// typed series ($type line/column/bar/pie/doughnut + data + label) to LiveCharts series. Pie/doughnut →
/// a <c>PieChart</c> (one slice per labelled value); line/column/bar → a <c>CartesianChart</c> with the
/// labels as the category X axis. The LiveCharts series share names with the framework's
/// (<c>LineSeries</c>/<c>ColumnSeries</c>/<c>PieSeries</c>), so they're fully qualified here.
/// </summary>
public sealed class ChartView : MauiView<ChartControl>
{
    protected override View CreateView()
    {
        var labels = CoerceLabels(Model.Labels);
        var series = CoerceSeries(Model.Series);
        var height = ToDouble(Model.Height, 300);
        var width = ToDouble(Model.Width, double.NaN);

        var isPie = series.Count > 0 && series.All(s => s.Type is "pie" or "doughnut");
        if (isPie)
        {
            var s0 = series[0];
            var slices = new List<LiveChartsCore.ISeries>();
            for (var i = 0; i < s0.Data.Length; i++)
                slices.Add(new LiveChartsCore.SkiaSharpView.PieSeries<double>
                {
                    Values = new[] { s0.Data[i] },
                    Name = i < labels.Count ? labels[i] : $"#{i + 1}",
                });
            var pie = new LiveChartsCore.SkiaSharpView.Maui.PieChart { Series = slices, HeightRequest = height };
            if (!double.IsNaN(width)) pie.WidthRequest = width;
            return pie;
        }

        var cartSeries = series.Select(s => (LiveChartsCore.ISeries)(s.Type == "line"
            ? new LiveChartsCore.SkiaSharpView.LineSeries<double> { Values = s.Data, Name = s.Label }
            : new LiveChartsCore.SkiaSharpView.ColumnSeries<double> { Values = s.Data, Name = s.Label })).ToList();
        var chart = new LiveChartsCore.SkiaSharpView.Maui.CartesianChart { Series = cartSeries, HeightRequest = height };
        if (!double.IsNaN(width)) chart.WidthRequest = width;
        if (labels.Count > 0)
            chart.XAxes = new[] { new LiveChartsCore.SkiaSharpView.Axis { Labels = labels } };
        return chart;
    }

    private static List<string> CoerceLabels(object? labels)
    {
        var result = new List<string>();
        if (labels is JsonElement { ValueKind: JsonValueKind.Array } arr)
            foreach (var el in arr.EnumerateArray())
                result.Add(el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString());
        else if (labels is IEnumerable<string> typed)
            result.AddRange(typed);
        return result;
    }

    // Each series JSON carries its $type discriminator (line/column/bar/pie/...) + data + label.
    private static List<(string Type, string Label, double[] Data)> CoerceSeries(object? series)
    {
        var result = new List<(string, string, double[])>();
        if (series is not JsonElement { ValueKind: JsonValueKind.Array } arr) return result;
        foreach (var el in arr.EnumerateArray())
        {
            var type = el.TryGetProperty("$type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "column" : "column";
            var label = el.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? "" : "";
            var data = el.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array
                ? d.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToArray()
                : Array.Empty<double>();
            result.Add((type, label, data));
        }
        return result;
    }

    private static double ToDouble(object? v, double dflt) =>
        v switch
        {
            null => dflt,
            double d => d,
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetDouble(),
            _ => double.TryParse(v.ToString(), out var p) ? p : dflt,
        };
}

/// <summary>Markdown editor → a native multi-line <see cref="Editor"/> bound to <c>Value</c> (writes back
/// through the pointer; honours Readonly). The Monaco preview/comments/track-changes panels are a later
/// wave — this gives native markdown editing.</summary>
public sealed class MarkdownEditorView : FormMauiView<MarkdownEditorControl>
{
    private Editor _editor = null!;
    protected override View CreateView()
    {
        _editor = new Editor
        {
            AutoSize = EditorAutoSizeOption.TextChanges, FontSize = 14, TextColor = Colors.White,
            Placeholder = MarkdownViewLogic.CoerceString(Model.Placeholder), IsReadOnly = AsFlag(Model.Readonly),
        };
        _editor.TextChanged += (_, _) => Write(Model.Value, _editor.Text);
        return _editor;
    }
    protected override void Bind() => BindValue<string>(Model.Value, v => _editor.Text = v ?? "");
    internal static bool AsFlag(object? v) => v is true || (v is JsonElement je && je.ValueKind == JsonValueKind.True);
}

/// <summary>Code editor → a native monospace <see cref="Editor"/> bound to <c>Value</c> with a language
/// caption (writes back through the pointer; honours Readonly). Monaco LSP/minimap are a later wave.</summary>
public sealed class CodeEditorView : FormMauiView<CodeEditorControl>
{
    private Editor _editor = null!;
    protected override View CreateView()
    {
        var lang = MarkdownViewLogic.CoerceString(Model.Language);
        _editor = new Editor
        {
            FontFamily = "Menlo", FontSize = 13, TextColor = Color.FromArgb("#E0E0E0"),
            AutoSize = EditorAutoSizeOption.TextChanges, IsReadOnly = MarkdownEditorView.AsFlag(Model.Readonly),
        };
        _editor.TextChanged += (_, _) => Write(Model.Value, _editor.Text);
        var caption = new Label { Text = string.IsNullOrWhiteSpace(lang) ? "code" : lang, FontSize = 10, TextColor = Color.FromArgb("#9A9A9A") };
        return new VerticalStackLayout { Spacing = 2, Children = { caption, _editor } };
    }
    protected override void Bind() => BindValue<string>(Model.Value, v => _editor.Text = v ?? "");
}

/// <summary>Diff editor → two side-by-side read-only monospace panels (original | modified) with labels —
/// the native counterpart of the Monaco side-by-side diff.</summary>
public sealed class DiffEditorView : MauiView<DiffEditorControl>
{
    protected override View CreateView()
    {
        var grid = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) } };
        grid.Add(Pane(Model.OriginalLabel, Model.OriginalContent), 0, 0);
        grid.Add(Pane(Model.ModifiedLabel, Model.ModifiedContent), 1, 0);
        return grid;
    }
    private static View Pane(string label, string content)
    {
        var stack = new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new Label { Text = label, FontSize = 11, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#C0C0C0") },
                new Label { Text = content, FontFamily = "Menlo", FontSize = 12, TextColor = Color.FromArgb("#E0E0E0") },
            },
        };
        return new Border
        {
            Padding = 8, BackgroundColor = Color.FromArgb("#1E1E1E"), StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Content = new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = stack },
        };
    }
}

/// <summary>User profile → a card: avatar (icon/initials) + display name + email + bio, tappable to the
/// user node. Native counterpart of the portal's profile card.</summary>
public sealed class UserProfileView : MauiView<UserProfileControl>
{
    protected override View CreateView()
    {
        var avatar = MauiImagery.ForSource(string.IsNullOrWhiteSpace(Model.Icon) ? Initials(Model.DisplayName) : Model.Icon, 64);
        avatar.WidthRequest = 64; avatar.HeightRequest = 64;

        var texts = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        texts.Children.Add(new Label { Text = Model.DisplayName, FontAttributes = FontAttributes.Bold, FontSize = 16, TextColor = Colors.White });
        if (!string.IsNullOrWhiteSpace(Model.Email)) texts.Children.Add(new Label { Text = Model.Email, FontSize = 12, TextColor = Color.FromArgb("#4ea1ff") });
        if (!string.IsNullOrWhiteSpace(Model.Bio)) texts.Children.Add(new Label { Text = Model.Bio, FontSize = 12, TextColor = Color.FromArgb("#C0C0C0") });

        var border = new Border
        {
            Padding = 14, BackgroundColor = Color.FromArgb("#2A2A2C"), StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Content = new HorizontalStackLayout { Spacing = 14, Children = { avatar, texts } },
        };
        if (!string.IsNullOrEmpty(Model.NodePath))
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => Stream?.Hub.ServiceProvider.GetService<IMauiNavigator>()?.NavigateTo(Model.NodePath, Model.DisplayName);
            border.GestureRecognizers.Add(tap);
        }
        return border;
    }

    private static string Initials(string? name)
    {
        var parts = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        return parts.Length == 1 ? parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant()
            : (parts[0][..1] + parts[1][..1]).ToUpperInvariant();
    }
}

/// <summary>Appearance picker → Light / Dark / System buttons that set the app theme
/// (<see cref="Application.UserAppTheme"/>). The preset-colour/direction options are a later wave.</summary>
public sealed class AppearanceView : MauiView<AppearanceControl>
{
    protected override View CreateView()
    {
        var row = new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { ThemeButton("Light", AppTheme.Light), ThemeButton("Dark", AppTheme.Dark), ThemeButton("System", AppTheme.Unspecified) },
        };
        return new VerticalStackLayout
        {
            Spacing = 6,
            Children = { new Label { Text = "Appearance", FontAttributes = FontAttributes.Bold, TextColor = Colors.White }, row },
        };
    }
    private static Button ThemeButton(string text, AppTheme theme)
    {
        var b = new Button { Text = text, BackgroundColor = Color.FromArgb("#3A3A3C"), TextColor = Colors.White, CornerRadius = 8 };
        b.Clicked += (_, _) => { if (Microsoft.Maui.Controls.Application.Current is { } app) app.UserAppTheme = theme; };
        return b;
    }
}

/// <summary>Item template → renders the control's <c>View</c> child area (the per-item template area). True
/// per-item iteration with item data-contexts is a later wave; this renders the template area natively.</summary>
public sealed class ItemTemplateView : MauiView<ItemTemplateControl>
{
    protected override View CreateView() =>
        Stream is not null ? (View)Renderer.RenderArea(Stream, "View") : new ContentView();
}

/// <summary>Layout-area definition → a tappable card with the area's thumbnail (light variant). Links to the
/// defined area; the title/description overlay is a later wave.</summary>
public sealed class LayoutAreaDefinitionView : MauiView<LayoutAreaDefinitionControl>
{
    protected override View CreateView()
    {
        var content = new VerticalStackLayout { Spacing = 6 };
        if (!string.IsNullOrWhiteSpace(Model.LightThumbnailUrl))
            content.Children.Add(new Image { Source = Model.LightThumbnailUrl, Aspect = Aspect.AspectFit, HeightRequest = 120 });
        content.Children.Add(new Label { Text = Model.Definition?.ToString() ?? "Layout area", FontSize = 12, TextColor = Color.FromArgb("#C0C0C0") });
        return new Border
        {
            Padding = 10, BackgroundColor = Color.FromArgb("#2A2A2C"), StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Content = content,
        };
    }
}

/// <summary>Mesh-node editor → a live, node-bound name field (writes back via
/// <c>GetMeshNodeStream(path).Update</c>) under a node-type caption. Rich typed-field editing is the
/// dedicated MeshNodeContentEditor; this is the generic by-path/by-type editor.</summary>
public sealed class MeshNodeEditorView : MauiView<MeshNodeEditorControl>
{
    private Entry _name = null!;
    private bool _suppress;
    protected override View CreateView()
    {
        var caption = new Label { Text = string.IsNullOrWhiteSpace(Model.NodeType) ? "Node" : Model.NodeType!, FontSize = 10, TextColor = Color.FromArgb("#9A9A9A") };
        _name = new Entry { Placeholder = "Name", TextColor = Colors.White };
        _name.TextChanged += (_, e) => { if (!_suppress) Persist(e.NewTextValue); };
        return new VerticalStackLayout { Spacing = 4, Children = { caption, _name } };
    }
    protected override void Bind()
    {
        if (Stream is null || string.IsNullOrEmpty(Model.NodePath)) return;
        var sub = Stream.Hub.GetMeshNodeStream(Model.NodePath).Where(n => n is not null)
            .Subscribe(node => MainThread.BeginInvokeOnMainThread(() =>
            {
                _suppress = true;
                try { _name.Text = node!.Name ?? ""; } finally { _suppress = false; }
            }));
        Disposables.Add(sub);
    }
    private void Persist(string? name)
    {
        if (Stream is null || string.IsNullOrEmpty(Model.NodePath)) return;
        Stream.Hub.GetMeshNodeStream(Model.NodePath).Update(n => n with { Name = name }).Subscribe(_ => { }, _ => { });
    }
}

/// <summary>Mesh-node role editor → a role <see cref="Picker"/> (from RoleOptions) + a Deny checkbox, gated
/// by CanEdit. Native UI for the role assignment; the access-assignment write-back is a later wave.</summary>
public sealed class MeshNodeRoleEditorView : MauiView<MeshNodeRoleEditorControl>
{
    protected override View CreateView()
    {
        var picker = new Picker { IsEnabled = Model.CanEdit, TextColor = Colors.White, Title = "Role" };
        foreach (var r in Model.RoleOptions) picker.Items.Add(r);
        var deny = new CheckBox { IsEnabled = Model.CanEdit };
        return new HorizontalStackLayout
        {
            Spacing = 8,
            Children =
            {
                picker,
                new Label { Text = "Deny", VerticalOptions = LayoutOptions.Center, TextColor = Color.FromArgb("#C0C0C0") },
                deny,
            },
        };
    }
}

/// <summary>Node import → a native form (target path + force / remove-missing flags) and an Import button
/// that dispatches the control's action (<see cref="MauiView.PostClick"/>). File/ZIP picking is a later wave.</summary>
public sealed class NodeImportView : MauiView<NodeImportControl>
{
    protected override View CreateView()
    {
        var target = new Entry { Placeholder = "Target path", Text = Model.TargetPath ?? "", TextColor = Colors.White };
        var force = new CheckBox { IsChecked = Model.Force };
        var removeMissing = new CheckBox { IsChecked = Model.RemoveMissing };
        var go = new Button { Text = "Import", BackgroundColor = Colors.RoyalBlue, TextColor = Colors.White, CornerRadius = 8 };
        go.Clicked += (_, _) => PostClick();
        return new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                new Label { Text = "Import nodes", FontAttributes = FontAttributes.Bold, TextColor = Colors.White },
                target,
                Flag("Force re-import", force),
                Flag("Remove missing", removeMissing),
                go,
            },
        };
    }
    internal static View Flag(string text, CheckBox cb) => new HorizontalStackLayout
    {
        Spacing = 6, Children = { cb, new Label { Text = text, VerticalOptions = LayoutOptions.Center, TextColor = Color.FromArgb("#C0C0C0") } },
    };
}

/// <summary>Node export → a native form (source + satellite-type toggles) and an Export button that
/// dispatches the control's action. The download is a later wave.</summary>
public sealed class NodeExportView : MauiView<NodeExportControl>
{
    protected override View CreateView()
    {
        var stack = new VerticalStackLayout { Spacing = 6 };
        stack.Children.Add(new Label { Text = $"Export {Model.NodeName ?? Model.SourcePath ?? "node"}", FontAttributes = FontAttributes.Bold, TextColor = Colors.White });
        foreach (var sat in Model.AvailableSatelliteTypes ?? Array.Empty<string>())
            stack.Children.Add(NodeImportView.Flag(sat, new CheckBox { IsChecked = true }));
        var go = new Button { Text = "Export", BackgroundColor = Colors.RoyalBlue, TextColor = Colors.White, CornerRadius = 8 };
        go.Clicked += (_, _) => PostClick();
        stack.Children.Add(go);
        return stack;
    }
}

/// <summary>Document export → a native form (title + PDF/DOCX format + include-children) and an Export
/// button that dispatches the control's action.</summary>
public sealed class ExportDocumentView : MauiView<ExportDocumentControl>
{
    protected override View CreateView()
    {
        var title = new Entry { Placeholder = "Title", Text = Model.NodeName ?? "", TextColor = Colors.White };
        var format = new Picker { Title = "Format", TextColor = Colors.White };
        format.Items.Add("pdf"); format.Items.Add("docx");
        format.SelectedItem = string.Equals(Model.DefaultFormat, "docx", StringComparison.OrdinalIgnoreCase) ? "docx" : "pdf";
        var stack = new VerticalStackLayout
        {
            Spacing = 6,
            Children = { new Label { Text = "Export document", FontAttributes = FontAttributes.Bold, TextColor = Colors.White }, title, format },
        };
        if (Model.HasDescendants)
            stack.Children.Add(NodeImportView.Flag("Include children", new CheckBox()));
        var go = new Button { Text = "Export", BackgroundColor = Colors.RoyalBlue, TextColor = Colors.White, CornerRadius = 8 };
        go.Clicked += (_, _) => PostClick();
        stack.Children.Add(go);
        return stack;
    }
}

/// <summary>File browser → a native header showing the collection path (full file listing + upload/delete is
/// a later wave that needs the content service surfaced natively).</summary>
public sealed class FileBrowserView : MauiView<FileBrowserControl>
{
    protected override View CreateView() => new VerticalStackLayout
    {
        Spacing = 4,
        Children =
        {
            new Label { Text = "Files", FontAttributes = FontAttributes.Bold, TextColor = Colors.White },
            new Label { Text = MarkdownViewLogic.CoerceString(Model.Path) ?? MarkdownViewLogic.CoerceString(Model.Collection) ?? "", FontSize = 12, TextColor = Color.FromArgb("#C0C0C0") },
        },
    };
}

/// <summary>Pivot grid → a native placeholder header (the full pivot rendering builds on the OSS data grid,
/// a later wave). Closes the gap so the control renders natively instead of the fallback label.</summary>
public sealed class PivotGridView : MauiView<PivotGridControl>
{
    protected override View CreateView() => new Border
    {
        Padding = 10, BackgroundColor = Color.FromArgb("#2A2A2C"), StrokeThickness = 0,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
        Content = new Label { Text = "Pivot grid", FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#C0C0C0") },
    };
}

/// <summary>Map → a native MAUI <see cref="Microsoft.Maui.Controls.Maps.Map"/> (MapKit on maccatalyst/iOS —
/// no Google API key): centers + zooms from <c>Options.Center</c>/<c>Zoom</c> and drops a pin per marker.
/// The AspNetCore-free counterpart of the portal's Google Maps JS embed.</summary>
public sealed class GoogleMapView : MauiView<GoogleMapControl>
{
    protected override View CreateView()
    {
        var map = new Microsoft.Maui.Controls.Maps.Map { HeightRequest = 300 };

        var center = Model.Options?.Center;
        if (center is not null)
        {
            var loc = new Microsoft.Maui.Devices.Sensors.Location(Convert.ToDouble(center.Lat), Convert.ToDouble(center.Lng));
            var zoom = Model.Options?.Zoom ?? 15;
            // Higher zoom → smaller visible radius (rough Web-Mercator-ish mapping).
            var km = Math.Max(0.2, 40000.0 / Math.Pow(2, zoom));
            map.MoveToRegion(Microsoft.Maui.Maps.MapSpan.FromCenterAndRadius(loc, Microsoft.Maui.Maps.Distance.FromKilometers(km)));
        }

        if (Model.Markers is not null)
            foreach (var m in Model.Markers)
                map.Pins.Add(new Microsoft.Maui.Controls.Maps.Pin
                {
                    Location = new Microsoft.Maui.Devices.Sensors.Location(Convert.ToDouble(m.Position.Lat), Convert.ToDouble(m.Position.Lng)),
                    Label = m.Title ?? "Marker",
                });
        return map;
    }
}

/// <summary>A notebook cell → native: the cell content (input) on a dark panel + its output below (green).
/// The notebook itself is an IContainerControl that renders these cells via the generic ContainerView.
/// Cell execution (run) + markdown-cell rendering (cell-type rides the skin) are later waves.</summary>
public sealed class NotebookCellView : MauiView<NotebookCellControl>
{
    protected override View CreateView()
    {
        // The cell carries Content (input) + Output (result); cell-type/language ride the skin.
        var content = MarkdownViewLogic.CoerceString(Model.Content) ?? "";
        var stack = new VerticalStackLayout { Spacing = 4 };
        if (!string.IsNullOrWhiteSpace(content))
            stack.Children.Add(MonoPanel(content, "#1E1E1E", "#E0E0E0"));
        var output = MarkdownViewLogic.CoerceString(Model.Output);
        if (!string.IsNullOrWhiteSpace(output))
            stack.Children.Add(MonoPanel(output!, "#16201A", "#98C379"));
        return stack;
    }

    private static View MonoPanel(string text, string bg, string fg) => new Border
    {
        Padding = 8, BackgroundColor = Color.FromArgb(bg), StrokeThickness = 0,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
        Content = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            Content = new Label { Text = text, FontFamily = "Menlo", FontSize = 12, TextColor = Color.FromArgb(fg) },
        },
    };
}

/// <summary>Tabular data → a header + rows (read-only this wave; sorting/virtualization later).</summary>
public sealed class DataGridView : MauiView<DataGridControl>
{
    private readonly List<PropertyColumnControl> _columns = new();
    private ContentView _header = null!;
    private VerticalStackLayout _body = null!;
    private Entry _filter = null!;
    private List<JsonElement> _rows = new();
    private int _sortCol = -1;
    private bool _sortDesc;

    protected override View CreateView()
    {
        _columns.AddRange(Model.Columns.OfType<PropertyColumnControl>());
        _filter = new Entry { Placeholder = "Filter…", FontSize = 12, TextColor = Colors.White };
        _filter.TextChanged += (_, _) => Rebuild();
        _header = new ContentView { Content = HeaderRow() };
        _body = new VerticalStackLayout { Spacing = 4 };
        return new VerticalStackLayout { Spacing = 4, Children = { _filter, _header, _body } };
    }

    protected override void Bind() => Bind<JsonElement>(Model.Data, rows =>
    {
        _rows = rows.ValueKind == JsonValueKind.Array ? rows.EnumerateArray().ToList() : new();
        Rebuild();
    });

    // Header cells are tappable → sort by that column, toggling asc/desc; an arrow marks the sort key.
    private View HeaderRow()
    {
        var h = new HorizontalStackLayout { Spacing = 12 };
        for (var i = 0; i < _columns.Count; i++)
        {
            var col = i;
            var arrow = _sortCol == col ? (_sortDesc ? " ▼" : " ▲") : "";
            var lbl = new Label
            {
                Text = (_columns[i].Title?.ToString() ?? _columns[i].Property ?? "") + arrow,
                WidthRequest = 120, FontAttributes = FontAttributes.Bold, TextColor = Colors.White,
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                if (_sortCol == col) _sortDesc = !_sortDesc; else { _sortCol = col; _sortDesc = false; }
                Rebuild();
            };
            lbl.GestureRecognizers.Add(tap);
            h.Children.Add(lbl);
        }
        return h;
    }

    private void Rebuild()
    {
        if (_body is null) return;
        _header.Content = HeaderRow();   // refresh the sort arrow
        _body.Children.Clear();
        IEnumerable<JsonElement> rows = _rows;
        var filter = _filter.Text?.Trim() ?? "";
        if (filter.Length > 0)
            rows = rows.Where(r => _columns.Any(c => CellText(r, c.Property).Contains(filter, StringComparison.OrdinalIgnoreCase)));
        if (_sortCol >= 0 && _sortCol < _columns.Count)
        {
            var prop = _columns[_sortCol].Property;
            rows = _sortDesc
                ? rows.OrderByDescending(r => CellText(r, prop), DataGridCellComparer.Instance)
                : rows.OrderBy(r => CellText(r, prop), DataGridCellComparer.Instance);
        }
        foreach (var row in rows)
            _body.Children.Add(Row(_columns.Select(c => CellText(row, c.Property)).ToArray(), bold: false));
    }

    private static string CellText(JsonElement row, string? property)
    {
        if (property is null || property.Length == 0 || row.ValueKind != JsonValueKind.Object) return "";
        var camel = char.ToLowerInvariant(property[0]) + property[1..];   // JSON is camelCase
        if (row.TryGetProperty(camel, out var v) || row.TryGetProperty(property, out v))
            return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
        return "";
    }

    private static View Row(string[] cells, bool bold)
    {
        var h = new HorizontalStackLayout { Spacing = 12 };
        foreach (var cell in cells)
            h.Children.Add(new Label
            {
                Text = cell,
                WidthRequest = 120,
                FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
            });
        return h;
    }
}

/// <summary>Sorts grid cells numerically when both values parse as numbers, else case-insensitively —
/// so a "Count" column sorts 2,10,100 not 10,100,2.</summary>
internal sealed class DataGridCellComparer : IComparer<string>
{
    public static readonly DataGridCellComparer Instance = new();
    public int Compare(string? x, string? y)
    {
        if (double.TryParse(x, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var nx)
            && double.TryParse(y, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ny))
            return nx.CompareTo(ny);
        return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Single-line text field → MAUI <see cref="Entry"/> (two-way).</summary>
public sealed class TextFieldView : FormMauiView<TextFieldControl>
{
    private Entry _entry = null!;
    protected override View CreateView()
    {
        _entry = new Entry();
        _entry.TextChanged += (_, _) => Write(Model.Data, _entry.Text);
        return _entry;
    }
    protected override void Bind() => BindValue<string>(Model.Data, v => _entry.Text = v ?? "");
}

/// <summary>Multi-line text area → MAUI <see cref="Editor"/> (two-way).</summary>
public sealed class TextAreaView : FormMauiView<TextAreaControl>
{
    private Editor _editor = null!;
    protected override View CreateView()
    {
        _editor = new Editor { AutoSize = EditorAutoSizeOption.TextChanges };
        _editor.TextChanged += (_, _) => Write(Model.Data, _editor.Text);
        return _editor;
    }
    protected override void Bind() => BindValue<string>(Model.Data, v => _editor.Text = v ?? "");
}

/// <summary>Boolean checkbox → MAUI <see cref="CheckBox"/> (two-way).</summary>
public sealed class CheckBoxView : FormMauiView<CheckBoxControl>
{
    private CheckBox _cb = null!;
    protected override View CreateView()
    {
        _cb = new CheckBox();
        _cb.CheckedChanged += (_, _) => Write(Model.Data, _cb.IsChecked);
        return _cb;
    }
    protected override void Bind() => BindValue<bool>(Model.Data, v => _cb.IsChecked = v);
}

/// <summary>Single-select dropdown → MAUI <see cref="Picker"/> (two-way; literal options this wave).</summary>
public sealed class SelectView : FormMauiView<SelectControl>
{
    private Picker _picker = null!;
    private List<(string Text, string? Value)> _options = new();

    protected override View CreateView()
    {
        _picker = new Picker();
        _picker.SelectedIndexChanged += (_, _) =>
        {
            if (_picker.SelectedIndex >= 0 && _picker.SelectedIndex < _options.Count)
                Write(Model.Data, _options[_picker.SelectedIndex].Value);
        };
        return _picker;
    }

    protected override void Bind()
    {
        // Options now coerce from the JsonElement round-trip too (were silently empty before).
        _options = CoerceOptions(Model.Options);
        _picker.Items.Clear();
        foreach (var (text, _) in _options) _picker.Items.Add(text);
        BindValue<object>(Model.Data, v =>
        {
            var idx = _options.FindIndex(o => string.Equals(o.Value, v?.ToString(), StringComparison.Ordinal));
            if (idx >= 0) _picker.SelectedIndex = idx;
        });
    }
}

/// <summary>Boolean toggle → MAUI <see cref="Switch"/> (two-way).</summary>
public sealed class SwitchView : FormMauiView<SwitchControl>
{
    private Switch _sw = null!;
    protected override View CreateView()
    {
        _sw = new Switch();
        _sw.Toggled += (_, _) => Write(Model.Data, _sw.IsToggled);
        return _sw;
    }
    protected override void Bind() => BindValue<bool>(Model.Data, v => _sw.IsToggled = v);
}

/// <summary>Progress (0–100) + message → MAUI <see cref="ProgressBar"/> with a caption.</summary>
public sealed class ProgressView : MauiView<ProgressControl>
{
    private ProgressBar _bar = null!;
    private Label _caption = null!;
    protected override View CreateView()
    {
        _bar = new ProgressBar();
        _caption = new Label();
        return new VerticalStackLayout { Spacing = 2, Children = { _caption, _bar } };
    }
    protected override void Bind()
    {
        Bind<object>(Model.Message, v => _caption.Text = v?.ToString() ?? "");
        Bind<double>(Model.Progress, v => _bar.Progress = Math.Clamp(v / 100.0, 0, 1));
    }
}

// ---- Wave 2 control views: details forms + layout ------------------------------------------------

/// <summary>Numeric field → MAUI <see cref="Entry"/> with a numeric keyboard (two-way). Writes the parsed
/// number back; non-numeric text is written verbatim so partial edits aren't lost.</summary>
public sealed class NumberFieldView : FormMauiView<NumberFieldControl>
{
    private Entry _entry = null!;
    protected override View CreateView()
    {
        _entry = new Entry { Keyboard = Keyboard.Numeric };
        _entry.TextChanged += (_, _) =>
        {
            if (double.TryParse(_entry.Text, out var d)) Write<object>(Model.Data, d);
            else if (string.IsNullOrEmpty(_entry.Text)) Write<object>(Model.Data, null);
        };
        return _entry;
    }
    protected override void Bind() => BindValue<object>(Model.Data, v => _entry.Text = v?.ToString() ?? "");
}

/// <summary>Date/time field → MAUI <see cref="DatePicker"/> (two-way).</summary>
public sealed class DateTimeView : FormMauiView<DateTimeControl>
{
    private DatePicker _picker = null!;
    protected override View CreateView()
    {
        _picker = new DatePicker();
        _picker.DateSelected += (_, e) => Write<object>(Model.Data, e.NewDate);
        return _picker;
    }
    // Bind via object to avoid the unconstrained-generic nullable-struct (DateTime?) ambiguity.
    protected override void Bind() => BindValue<object>(Model.Data, v =>
    {
        if (v is DateTime d) _picker.Date = d;
        else if (v is DateTimeOffset dto) _picker.Date = dto.DateTime;
        else if (v is string s && DateTime.TryParse(s, out var p)) _picker.Date = p;
    });
}

/// <summary>Date-only field → MAUI <see cref="DatePicker"/> (two-way). Sibling of <see cref="DateTimeView"/>
/// for the framework's <see cref="DateControl"/> (date without a time component).</summary>
public sealed class DateView : FormMauiView<DateControl>
{
    private DatePicker _picker = null!;
    protected override View CreateView()
    {
        _picker = new DatePicker();
        _picker.DateSelected += (_, e) => Write<object>(Model.Data, e.NewDate);
        return _picker;
    }
    protected override void Bind() => BindValue<object>(Model.Data, v =>
    {
        if (v is DateTime d) _picker.Date = d;
        else if (v is DateTimeOffset dto) _picker.Date = dto.DateTime;
        else if (v is string s && DateTime.TryParse(s, out var p)) _picker.Date = p;
    });
}

/// <summary>Single-choice radio group → a vertical stack of MAUI <see cref="RadioButton"/>s sharing a group
/// (two-way). Mirrors <see cref="SelectView"/> but presents the options inline instead of in a dropdown.</summary>
public sealed class RadioGroupView : FormMauiView<RadioGroupControl>
{
    private VerticalStackLayout _stack = null!;
    private List<(string Text, string? Value)> _options = new();
    private readonly List<RadioButton> _buttons = new();
    // A stable group name keeps the radios mutually exclusive (MAUI scopes selection by GroupName).
    private readonly string _group = "rg-" + Guid.NewGuid().ToString("N");

    protected override View CreateView() => _stack = new VerticalStackLayout { Spacing = 4 };

    protected override void Bind()
    {
        _options = CoerceOptions(Model.Options);
        _stack.Children.Clear();
        _buttons.Clear();
        foreach (var (text, value) in _options)
        {
            var rb = new RadioButton { Content = text, GroupName = _group, Value = value };
            rb.CheckedChanged += (_, e) => { if (e.Value) Write(Model.Data, value); };
            _buttons.Add(rb);
            _stack.Children.Add(rb);
        }
        BindValue<object>(Model.Data, v =>
        {
            var idx = _options.FindIndex(o => string.Equals(o.Value, v?.ToString(), StringComparison.Ordinal));
            for (var i = 0; i < _buttons.Count; i++) _buttons[i].IsChecked = i == idx;
        });
    }
}

/// <summary>Numeric slider → MAUI <see cref="Slider"/> over [Min, Max]. The framework
/// <see cref="SliderControl"/> carries no data pointer (Min/Max/Step only), so this renders the range
/// visually — read-only, matching the control's shape (Blazor has no slider component at all).</summary>
public sealed class SliderView : MauiView<SliderControl>
{
    protected override View CreateView() => new Slider
    {
        Minimum = Model.Min,
        Maximum = Math.Max(Model.Max, Model.Min + 1),
    };
}

/// <summary>Spacer → a transparent, flexible <see cref="BoxView"/> that absorbs free space in its parent
/// stack/grid (the layout-skin spacer).</summary>
public sealed class SpacerView : MauiView<SpacerControl>
{
    protected override View CreateView() => new BoxView
    {
        Color = Colors.Transparent,
        HorizontalOptions = LayoutOptions.Fill,
        VerticalOptions = LayoutOptions.Fill,
    };
}

/// <summary>Exception message → a red caption with the message and (when present) type + stack trace,
/// the native counterpart of Blazor's error message bar.</summary>
public sealed class ExceptionView : MauiView<ExceptionControl>
{
    protected override View CreateView()
    {
        var stack = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(8) };
        stack.Children.Add(new Label { Text = Model.Message, TextColor = Colors.OrangeRed, FontAttributes = FontAttributes.Bold });
        if (!string.IsNullOrWhiteSpace(Model.Type))
            stack.Children.Add(new Label { Text = Model.Type, TextColor = Colors.OrangeRed, FontSize = 11 });
        if (!string.IsNullOrWhiteSpace(Model.StackTrace))
            stack.Children.Add(new Label { Text = Model.StackTrace, TextColor = Colors.Gray, FontSize = 10 });
        return stack;
    }
}

/// <summary>Combobox → MAUI <see cref="Picker"/> (two-way; native Picker has no free-type filter — a
/// later Monaco/autocomplete wave can add that).</summary>
public sealed class ComboboxView : FormMauiView<ComboboxControl>
{
    private Picker _picker = null!;
    private List<(string Text, string? Value)> _options = new();
    protected override View CreateView()
    {
        _picker = new Picker();
        _picker.SelectedIndexChanged += (_, _) =>
        {
            if (_picker.SelectedIndex >= 0 && _picker.SelectedIndex < _options.Count)
                Write(Model.Data, _options[_picker.SelectedIndex].Value);
        };
        return _picker;
    }
    protected override void Bind()
    {
        // Free-type filter is still a later wave (native Picker has none); options now at least populate.
        _options = CoerceOptions(Model.Options);
        _picker.Items.Clear();
        foreach (var (text, _) in _options) _picker.Items.Add(text);
        BindValue<object>(Model.Data, v =>
        {
            var idx = _options.FindIndex(o => string.Equals(o.Value, v?.ToString(), StringComparison.Ordinal));
            if (idx >= 0) _picker.SelectedIndex = idx;
        });
    }
}

/// <summary>Listbox → MAUI <see cref="Picker"/> (two-way single-select this wave).</summary>
public sealed class ListboxView : FormMauiView<ListboxControl>
{
    private Picker _picker = null!;
    private List<(string Text, string? Value)> _options = new();
    protected override View CreateView()
    {
        _picker = new Picker();
        _picker.SelectedIndexChanged += (_, _) =>
        {
            if (_picker.SelectedIndex >= 0 && _picker.SelectedIndex < _options.Count)
                Write(Model.Data, _options[_picker.SelectedIndex].Value);
        };
        return _picker;
    }
    protected override void Bind()
    {
        _options = CoerceOptions(Model.Options);
        _picker.Items.Clear();
        foreach (var (text, _) in _options) _picker.Items.Add(text);
        BindValue<object>(Model.Data, v =>
        {
            var idx = _options.FindIndex(o => string.Equals(o.Value, v?.ToString(), StringComparison.Ordinal));
            if (idx >= 0) _picker.SelectedIndex = idx;
        });
    }
}

/// <summary>Layout grid → a wrapping MAUI <see cref="FlexLayout"/> of its child areas (a real flowing grid
/// rather than the generic vertical stack the container fallback would give).</summary>
public sealed class LayoutGridView : MauiView<LayoutGridControl>
{
    protected override View CreateView()
    {
        var flex = new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Start,
        };
        if (Stream is not null && Control is IContainerControl container)
            foreach (var named in container.Areas)
            {
                var child = Renderer.RenderArea(Stream, named.Area.ToString()!);
                child.Margin = new Thickness(4);
                flex.Children.Add(child);
            }
        return flex;
    }
}

/// <summary>
/// Tabs → a header button row (the tab "title menu") over a content area that swaps to the selected
/// tab's child area. The tab labels come from each child area's <see cref="TabSkin"/> (the canonical
/// label the skinned <see cref="TabsControl"/> attaches), and the active tab is highlighted. The first
/// tab is selected on build, so the catalog shows content immediately (no eternal spinner).
/// </summary>
public sealed class TabsView : MauiView<TabsControl>
{
    protected override View CreateView()
    {
        var headers = new HorizontalStackLayout { Spacing = 4 };
        var content = new ContentView();
        var areas = (Control as IContainerControl)?.Areas.ToList() ?? new();
        var buttons = new List<Button>();

        void Select(int idx)
        {
            if (Stream is null || idx < 0 || idx >= areas.Count) return;
            content.Content = Renderer.RenderArea(Stream, areas[idx].Area.ToString()!);
            for (var i = 0; i < buttons.Count; i++)
            {
                var active = i == idx;
                buttons[i].FontAttributes = active ? FontAttributes.Bold : FontAttributes.None;
                buttons[i].TextColor = active ? Colors.RoyalBlue : Colors.Gray;
            }
        }

        for (var i = 0; i < areas.Count; i++)
        {
            var idx = i;
            var header = new Button
            {
                Text = TabLabel(areas[i]),
                BackgroundColor = Colors.Transparent,
                Padding = new Thickness(8, 4),
            };
            header.Clicked += (_, _) => Select(idx);
            buttons.Add(header);
            headers.Children.Add(header);
        }

        if (areas.Count > 0) Select(0);

        return new VerticalStackLayout
        {
            Spacing = 6,
            Children = { new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = headers }, content },
        };
    }

    // The tab label is the area's TabSkin label (what TabsControl.CreateItemSkin attaches and the
    // Blazor tab menu reads). Fall back to the area Id, then the area path, so a label always shows.
    private static string TabLabel(NamedAreaControl area)
        => area.Skins.OfType<TabSkin>().FirstOrDefault()?.Label?.ToString()
           ?? area.Id?.ToString()
           ?? area.Area?.ToString()
           ?? "";
}

/// <summary>
/// Embedded remote layout area (a <see cref="LayoutAreaControl"/>, e.g. the home page's bottom chat
/// composer) → the existing <see cref="LayoutAreaView"/> over the owning node's address. Resolves the
/// workspace + address off the current area's hub and reuses the same GetRemoteStream path the node
/// pages use; a same-hub address renders via the local ctor.
/// </summary>
public sealed class LayoutAreaControlView : MauiView<LayoutAreaControl>
{
    protected override View CreateView()
    {
        if (Stream is null) return new ContentView();
        var workspace = Stream.Hub.GetWorkspace();
        Address address = Model.Address?.ToString() ?? "";
        return address.Equals(workspace.Hub.Address)
            ? new LayoutAreaView(workspace, Model.Reference, Renderer)
            : new LayoutAreaView(workspace, address, Model.Reference, Renderer);
    }
}

// ---- Wave 2 control views: node-bound editor --------------------------------------------------------

/// <summary>
/// Native, cache-bound editor for a mesh node's content — the AspNetCore-free counterpart of Blazor's
/// <c>MeshNodeContentEditorView</c>. Binds DIRECTLY to the node stream (<c>Hub.GetMeshNodeStream(NodePath)</c>):
/// reads stay live with the node and every field edit writes back through <c>GetMeshNodeStream(NodePath)
/// .Update(...)</c> as a per-field read-modify-write patch. ONE source of truth (the node stream) — NO
/// <c>/data</c> replica, NO debounced save loop (the forbidden replicate-then-save antipattern). The fields
/// are declared on the control (reflected on the backend), so the view needs no client type registry.
/// </summary>
public sealed class MeshNodeContentEditorView : MauiView<MeshNodeContentEditorControl>
{
    private VerticalStackLayout _root = null!;
    private string _path = "";
    private bool _canEdit = true;
    private bool _suppress;          // true while loading echoed node state → don't re-persist
    private string? _focusedKey;     // the field the user is actively typing → don't clobber it
    // One loader per field key: applies the latest node content to that field's native control.
    private readonly Dictionary<string, Action<MeshNode>> _loaders = new();

    protected override View CreateView()
    {
        _path = Model.NodePath ?? "";
        _canEdit = Model.CanEdit;
        _root = new VerticalStackLayout { Spacing = 8 };
        foreach (var f in Model.Fields)
            _root.Children.Add(BuildField(f));
        return _root;
    }

    protected override void Bind()
    {
        if (Stream is null || string.IsNullOrEmpty(_path)) return;
        // Bind to the node stream — reads stay live; reuse the shared cache handle.
        var sub = Stream.Hub.GetMeshNodeStream(_path)
            .Where(n => n is not null)
            .Subscribe(node => MainThread.BeginInvokeOnMainThread(() =>
            {
                _suppress = true;
                try { foreach (var load in _loaders.Values) load(node!); }
                finally { _suppress = false; }
            }));
        Disposables.Add(sub);
    }

    private View BuildField(MeshNodeEditorField f)
    {
        var label = new Label { Text = f.Label, FontSize = 12, TextColor = Colors.Gray };
        View input;
        switch (f.Kind)
        {
            case MeshNodeEditorFieldKind.Bool:
            {
                var cb = new CheckBox { IsEnabled = _canEdit };
                cb.CheckedChanged += (_, e) => { if (!_suppress) Persist(f.Key, JsonValue.Create(e.Value)); };
                _loaders[f.Key] = node =>
                {
                    var v = FieldValue(node, f.Key);
                    cb.IsChecked = v is JsonValue jb && jb.TryGetValue<bool>(out var b) && b;
                };
                input = cb;
                break;
            }
            case MeshNodeEditorFieldKind.Enum:
            {
                var picker = new Picker { IsEnabled = _canEdit };
                foreach (var o in f.Options) picker.Items.Add(o);
                picker.SelectedIndexChanged += (_, _) =>
                {
                    if (_suppress) return;
                    var val = picker.SelectedIndex >= 0 ? f.Options[picker.SelectedIndex] : null;
                    Persist(f.Key, val is null ? null : JsonValue.Create(val));
                };
                _loaders[f.Key] = node =>
                {
                    var v = FieldValue(node, f.Key)?.ToString();
                    picker.SelectedIndex = v is null ? -1 : f.Options.IndexOf(v);
                };
                input = picker;
                break;
            }
            default: // Text
            {
                var entry = new Entry { IsEnabled = _canEdit };
                entry.Focused += (_, _) => _focusedKey = f.Key;
                entry.Unfocused += (_, _) => { if (_focusedKey == f.Key) _focusedKey = null; };
                entry.TextChanged += (_, e) =>
                {
                    if (!_suppress)
                        Persist(f.Key, string.IsNullOrEmpty(e.NewTextValue) ? null : JsonValue.Create(e.NewTextValue));
                };
                _loaders[f.Key] = node =>
                {
                    if (f.Key == _focusedKey) return; // don't clobber the active edit with an echo
                    var v = FieldValue(node, f.Key);
                    entry.Text = v is JsonValue js ? js.ToString() : v?.ToString() ?? "";
                };
                input = entry;
                break;
            }
        }
        return new VerticalStackLayout { Spacing = 2, Children = { label, input } };
    }

    private JsonNode? FieldValue(MeshNode node, string key) => ToJsonObject(node.Content)?[key];

    /// <summary>Per-field read-modify-write straight to the node via the cache: re-read the latest content
    /// inside the lambda and set ONLY this field, so concurrent writers / hidden fields aren't clobbered.</summary>
    private void Persist(string key, JsonNode? value)
    {
        if (!_canEdit || Stream is null || string.IsNullOrEmpty(_path)) return;
        var opts = Stream.Hub.JsonSerializerOptions;
        var logger = Stream.Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<MeshNodeContentEditorView>();
        Stream.Hub.GetMeshNodeStream(_path)
            .Update(node =>
            {
                var obj = ToJsonObject(node.Content) ?? new JsonObject();
                obj[key] = value is null ? null : JsonNode.Parse(value.ToJsonString());
                return node with { Content = JsonSerializer.SerializeToElement<object>(obj, opts) };
            })
            .Subscribe(_ => { }, ex => logger?.LogWarning(ex,
                "MeshNodeContentEditor: persist failed for {Path} field {Key}", _path, key));
    }

    private JsonObject? ToJsonObject(object? content)
    {
        if (content is null) return null;
        try
        {
            return content is JsonElement je
                ? JsonNode.Parse(je.GetRawText()) as JsonObject
                : JsonSerializer.SerializeToNode(content, Stream!.Hub.JsonSerializerOptions) as JsonObject;
        }
        catch { return null; }
    }
}

// ---- Wave 2 control views: query-driven (mesh node picker + catalog) ------------------------------

/// <summary>
/// Mesh node picker → a search box + a live result list (queried via <c>hub.GetQuery</c>); picking a row
/// writes the node's PATH back to the bound pointer. Mirrors the Blazor MeshNodePickerView's behaviour
/// (search appended to each of the control's queries; results de-duplicated by path), natively.
/// </summary>
public sealed class MeshNodePickerView : FormMauiView<MeshNodePickerControl>
{
    private Entry _search = null!;
    private VerticalStackLayout _results = null!;
    private Label _selected = null!;
    private IDisposable? _searchSub;

    protected override View CreateView()
    {
        _selected = new Label { FontSize = 12, TextColor = Colors.Gray };
        _search = new Entry { Placeholder = Model.Placeholder?.ToString() ?? "Search nodes…" };
        _search.TextChanged += (_, _) => RunSearch(_search.Text);
        _results = new VerticalStackLayout { Spacing = 2 };
        RunSearch(null);   // initial candidates
        return new VerticalStackLayout { Spacing = 4, Children = { _selected, _search, _results } };
    }

    protected override void Bind() =>
        BindValue<object>(Model.Data, v => _selected.Text = v is null ? "(no node selected)" : $"selected: {v}");

    private void RunSearch(string? text)
    {
        if (Stream is null) return;
        var baseQueries = Model.Queries is { Length: > 0 } q ? q : new[] { "" };
        var queries = baseQueries
            .Select(b => string.IsNullOrWhiteSpace(text) ? b : $"{b} {text}".Trim())
            .ToArray();

        _searchSub?.Dispose();   // drop the previous search; no accumulation
        _searchSub = Stream.Hub.GetQuery("picker:" + string.Join("|", queries), queries)
            .Take(1)
            .Subscribe(nodes => MainThread.BeginInvokeOnMainThread(() => RenderResults(nodes)));
        Disposables.Add(_searchSub);
    }

    private void RenderResults(IEnumerable<MeshNode> nodes)
    {
        _results.Children.Clear();
        var max = int.TryParse(Model.MaxResults?.ToString(), out var m) ? m : 20;
        foreach (var node in nodes.DistinctBy(n => n.Path).Take(max))
        {
            var n = node;
            var btn = new Button { Text = n.Name ?? n.Path, HorizontalOptions = LayoutOptions.Fill };
            btn.Clicked += (_, _) => { Write<object>(Model.Data, n.Path); _selected.Text = $"selected: {n.Path}"; };
            _results.Children.Add(btn);
        }
    }
}

/// <summary>
/// Catalog / mesh-node search → an optional search box (the <c>VisibleQuery</c>) fused with the
/// always-applied <c>HiddenQuery</c>, queried LIVE via <c>hub.GetQuery</c> and rendered as a list of
/// node cards. When the result set is empty it shows the control's empty state — a message (when
/// <c>ShowEmptyMessage</c>) and, when a <c>CreateNodeType</c> is configured, a "➕ New" affordance that
/// creates the node via the framework <see cref="IMeshService"/>. The native counterpart of the Blazor
/// MeshSearchView (thumbnail grid); drill-down navigation is a later wave.
/// </summary>
public sealed class MeshSearchView : MauiView<MeshSearchControl>
{
    private Entry? _search;
    private VerticalStackLayout _results = null!;
    private string _hidden = "";
    private IDisposable? _searchSub;

    protected override View CreateView()
    {
        _results = new VerticalStackLayout { Spacing = 4 };
        var root = new VerticalStackLayout { Spacing = 8 };
        // Honor ShowSearchBox: embedded sections (Spaces / Last Read / …) hide the box.
        if (AsBool(Model.ShowSearchBox, defaultValue: true))
        {
            _search = new Entry { Placeholder = Model.Placeholder?.ToString() ?? "Search…" };
            _search.TextChanged += (_, _) => RunSearch();
            root.Children.Add(_search);
        }
        root.Children.Add(_results);
        return root;
    }

    protected override void Bind()
    {
        Bind<object>(Model.HiddenQuery, v => { _hidden = v?.ToString() ?? ""; RunSearch(); });
        Bind<object>(Model.VisibleQuery, v =>
        {
            if (_search is not null && v is not null && string.IsNullOrEmpty(_search.Text)) _search.Text = v.ToString();
        });
    }

    private void RunSearch()
    {
        if (Stream is null) return;
        var parts = new[] { _hidden, _search?.Text }.Where(s => !string.IsNullOrWhiteSpace(s));
        var query = string.Join(" ", parts).Trim();
        if (query.Length == 0) { _results.Children.Clear(); return; }

        // LIVE: stay subscribed so the catalog updates as nodes change (e.g. after a create). The
        // previous subscription is disposed on each re-query so there is no accumulation.
        _searchSub?.Dispose();
        _searchSub = Stream.Hub.GetQuery("search:" + query, query)
            .Subscribe(nodes => MainThread.BeginInvokeOnMainThread(() => RenderResults(nodes)));
        Disposables.Add(_searchSub);
    }

    private void RenderResults(IEnumerable<MeshNode> nodes)
    {
        _results.Children.Clear();
        var list = nodes.ToList();
        if (list.Count == 0) { RenderEmptyState(); return; }
        foreach (var node in list.Take(50))
            _results.Children.Add(Card(node));
    }

    private static View Card(MeshNode node) => new Border
    {
        Padding = 8,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
        Stroke = Colors.Gray,
        StrokeThickness = 1,
        Content = new VerticalStackLayout
        {
            Children =
            {
                new Label { Text = node.Name ?? node.Path, FontAttributes = FontAttributes.Bold },
                new Label { Text = node.NodeType ?? "", FontSize = 11, TextColor = Colors.Gray },
            },
        },
    };

    private void RenderEmptyState()
    {
        var createType = Model.CreateNodeType?.ToString();
        var hasCreate = !string.IsNullOrEmpty(createType);
        if (!AsBool(Model.ShowEmptyMessage, defaultValue: true) && !hasCreate) return;

        var stack = new VerticalStackLayout { Spacing = 10, Padding = new Thickness(4, 16) };
        if (AsBool(Model.ShowEmptyMessage, defaultValue: true))
            stack.Children.Add(new Label { Text = "Nothing here yet.", TextColor = Colors.Gray });
        if (hasCreate)
        {
            var btn = new Button
            {
                Text = $"➕ New {createType}",
                FontSize = 15,
                BackgroundColor = Colors.RoyalBlue,
                TextColor = Colors.White,
                CornerRadius = 8,
                Padding = new Thickness(16, 6),
                HorizontalOptions = LayoutOptions.Start,
            };
            btn.Clicked += (_, _) => CreateNode(createType!);
            stack.Children.Add(btn);
        }
        _results.Children.Add(stack);
    }

    // The framework create primitive: a real node of CreateNodeType under CreateNamespace. The live
    // query above re-renders when the new node lands. Errors are logged, never swallowed.
    private void CreateNode(string nodeType)
    {
        var ns = Model.CreateNamespace?.ToString();
        if (Stream is null || string.IsNullOrEmpty(ns)) return;
        var meshService = Stream.Hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null) return;
        var logger = Stream.Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<MeshSearchView>();
        var node = new MeshNode(Guid.NewGuid().ToString("N")[..8], ns) { NodeType = nodeType, Name = $"New {nodeType}" };
        Disposables.Add(meshService.CreateNode(node).Subscribe(
            _ => { },
            ex => logger?.LogWarning(ex, "[MeshSearchView] create {NodeType} in {Namespace} failed", nodeType, ns)));
    }

    // Coerce a control flag that may arrive as a literal bool or a JSON bool (post stream round-trip).
    private static bool AsBool(object? value, bool defaultValue) => value switch
    {
        null => defaultValue,
        bool b => b,
        JsonElement je when je.ValueKind == JsonValueKind.True => true,
        JsonElement je when je.ValueKind == JsonValueKind.False => false,
        _ => bool.TryParse(value.ToString(), out var p) ? p : defaultValue,
    };
}

// ---- Chat: agent-backed thread message bubble ----------------------------------------------------

/// <summary>
/// A single thread message as a native chat bubble — the AspNetCore-free counterpart of Blazor's
/// <c>ThreadMessageBubbleView</c>. Role drives alignment + colour (user → right/blue, assistant → left/grey);
/// the body <c>Text</c> is data-bound (a <see cref="JsonPointerReference"/>) so it refines in place while the
/// agent streams. While executing it shows a spinner + the live <c>ExecutionStatus</c>; a footer chips the
/// author, model, and timestamp. (Tool-call chips + markdown body + edit/resubmit are later refinements;
/// this renders the conversation natively, which the agent chat needs first.)
/// </summary>
public sealed class ThreadMessageBubbleView : MauiView<ThreadMessageBubbleControl>
{
    private Border _border = null!;
    private Label _author = null!;
    private Label _body = null!;
    private ActivityIndicator _spinner = null!;
    private Label _status = null!;
    private Label _footer = null!;
    private HorizontalStackLayout _statusRow = null!;

    protected override View CreateView()
    {
        _author = new Label { FontSize = 11, FontAttributes = FontAttributes.Bold };
        _body = new Label { TextColor = Colors.White, LineBreakMode = LineBreakMode.WordWrap };
        _spinner = new ActivityIndicator { IsVisible = false, IsRunning = false, Color = Colors.White, HeightRequest = 14, WidthRequest = 14 };
        _status = new Label { FontSize = 11, TextColor = Color.FromArgb("#C0C0C0") };
        _statusRow = new HorizontalStackLayout { Spacing = 6, IsVisible = false, Children = { _spinner, _status } };
        _footer = new Label { FontSize = 10, TextColor = Color.FromArgb("#9A9A9A"), IsVisible = false };

        var content = new VerticalStackLayout { Spacing = 4, Children = { _author, _body, _statusRow, _footer } };
        _border = new Border
        {
            Padding = new Thickness(10, 6),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Content = content,
        };
        ApplyRole(Model.Role, Model.AuthorName);   // initial; refined from the node in path-bound mode
        return _border;
    }

    protected override void Bind()
    {
        // Dominant mode: a path-bound bubble subscribes to the message node and renders Role/Text/status
        // live as the agent streams (Status flips Streaming→Completed). Legacy callers pass Text/IsExecuting
        // pointers + a literal Role instead.
        if (!string.IsNullOrEmpty(Model.NodePath) && Stream is not null)
        {
            var sub = Stream.Hub.GetMeshNodeStream(Model.NodePath)
                .Where(n => n is not null)
                .Subscribe(node => MainThread.BeginInvokeOnMainThread(() => ApplyNode(node!)));
            Disposables.Add(sub);
            return;
        }

        Bind<string>(Model.Text, v => _body.Text = v ?? "");
        Bind<bool>(Model.IsExecuting, SetExecuting, defaultValue: false);
        Bind<string>(Model.ExecutionStatus, v => _status.Text = v ?? "");
        SetFooter(Model.ModelName, Model.Timestamp);
    }

    // Apply the live message-node content (path-bound mode) via the unit-tested MauiChatProjection.
    private void ApplyNode(MeshNode node)
    {
        if (ToJsonElement(node.Content) is not { } content) return;
        var m = MauiChatProjection.ReadMessage(content);
        ApplyRole(m.Role, m.AuthorName);
        _body.Text = m.Text;
        SetExecuting(m.IsStreaming);
        SetFooter(m.ModelName, m.Timestamp);
    }

    private void ApplyRole(string? role, string? authorName)
    {
        var isUser = string.Equals(role, "user", StringComparison.OrdinalIgnoreCase);
        _border.HorizontalOptions = isUser ? LayoutOptions.End : LayoutOptions.Start;
        _border.Margin = new Thickness(isUser ? 40 : 0, 2, isUser ? 0 : 40, 2);
        _border.BackgroundColor = isUser ? Colors.RoyalBlue : Color.FromArgb("#3A3A3C");
        _author.TextColor = isUser ? Colors.White : Color.FromArgb("#C0C0C0");
        _author.Text = string.IsNullOrWhiteSpace(authorName) ? (isUser ? "You" : "Assistant") : authorName;
    }

    private void SetExecuting(bool executing)
    {
        _statusRow.IsVisible = executing;
        _spinner.IsVisible = executing;
        _spinner.IsRunning = executing;
    }

    private void SetFooter(string? model, DateTime? ts)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(model)) parts.Add(model!);
        if (ts is { } t) parts.Add(t.ToLocalTime().ToString("t"));
        _footer.Text = string.Join(" · ", parts);
        _footer.IsVisible = parts.Count > 0;
    }

    private JsonElement? ToJsonElement(object? content)
    {
        if (content is null || Stream is null) return null;
        try
        {
            return content is JsonElement je ? je : JsonSerializer.SerializeToElement(content, Stream.Hub.JsonSerializerOptions);
        }
        catch { return null; }
    }
}

/// <summary>
/// The agent-backed chat thread → native — the AspNetCore-free counterpart of Blazor's ThreadChat view.
/// Binds the data-bound <see cref="ThreadChatControl.ThreadViewModel"/> (or a direct
/// <see cref="ThreadChatControl.ThreadPath"/>), renders one path-bound <see cref="ThreadMessageBubbleView"/>
/// per message (each self-updates as the agent streams), shows queued (pending) user messages immediately,
/// and submits new messages through the canonical <c>hub.SubmitMessage(threadPath, text)</c>
/// (HubThreadExtensions) — no bespoke request type. The message list rebuilds only when the set of message
/// IDs / pending texts changes, so streaming-text updates ride each bubble's own node subscription.
/// </summary>
public sealed class ThreadChatView : MauiView<ThreadChatControl>
{
    private ScrollView _scroll = null!;
    private VerticalStackLayout _messages = null!;
    private Editor _composer = null!;
    private Button _send = null!;
    private Label _status = null!;
    private string? _threadPath;
    private List<string> _renderedKeys = new();   // ids + ("pending:" + text), to skip redundant rebuilds

    protected override View CreateView()
    {
        _messages = new VerticalStackLayout { Spacing = 8 };
        _scroll = new ScrollView { Content = _messages, VerticalOptions = LayoutOptions.Fill };
        _status = new Label { FontSize = 11, TextColor = Colors.Gray, IsVisible = false };

        _composer = new Editor
        {
            Placeholder = "Message…", FontSize = 15, TextColor = Colors.White,
            AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 64,
            AutomationId = "chat-composer",   // stable selector for the Appium E2E
        };
        _send = new Button { Text = "Send", BackgroundColor = Colors.RoyalBlue, TextColor = Colors.White, CornerRadius = 8, AutomationId = "chat-send" };
        _send.Clicked += (_, _) => Submit();
        var composerRow = new VerticalStackLayout
        {
            Spacing = 6,
            Children = { _composer, new HorizontalStackLayout { HorizontalOptions = LayoutOptions.End, Children = { _send } } },
        };

        var grid = new Grid
        {
            RowSpacing = 8, Padding = 8,
            RowDefinitions = { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) },
        };
        grid.Add(_scroll, 0, 0);
        grid.Add(_status, 0, 1);
        grid.Add(composerRow, 0, 2);
        return grid;
    }

    protected override void Bind()
    {
        // Direct-path mode (side panel/dashboard): read the thread node itself.
        if (!string.IsNullOrEmpty(Model.ThreadPath) && Model.ThreadViewModel is null && Stream is not null)
        {
            _threadPath = Model.ThreadPath;
            var sub = Stream.Hub.GetMeshNodeStream(Model.ThreadPath)
                .Where(n => n is not null)
                .Subscribe(node => MainThread.BeginInvokeOnMainThread(() => ApplyThreadNode(node!)));
            Disposables.Add(sub);
            return;
        }
        // Data-bound view-model mode (the layout-area thread view): the area pushes a ThreadViewModel snapshot.
        Bind<object>(Model.ThreadViewModel, vm =>
        {
            if (vm is JsonElement je) ApplyViewModel(je);
        });
    }

    // Both modes project through the unit-tested MauiChatProjection (same JSON keys as the data-section VM).
    private void ApplyViewModel(JsonElement vm)
    {
        var s = MauiChatProjection.ReadThreadViewModel(vm);
        RenderState(s.ThreadPath ?? _threadPath, s.MessageIds.ToList(), s.PendingTexts.ToList(), s.IsExecuting, s.ExecutionStatus);
    }

    private void ApplyThreadNode(MeshNode node)
    {
        var s = ToJsonElement(node.Content) is { } content
            ? MauiChatProjection.ReadThreadViewModel(content)
            : new ThreadChatState(null, [], [], false, null);
        RenderState(_threadPath ?? node.Path, s.MessageIds.ToList(), new List<string>(), s.IsExecuting, s.ExecutionStatus);
    }

    private void RenderState(string? threadPath, List<string> ids, List<string> pending, bool executing, string? status)
    {
        _threadPath = threadPath;
        // Rebuild the list only when the set of bubbles changes — streaming text rides each bubble's own sub.
        var keys = ids.Concat(pending.Select(p => "pending:" + p)).ToList();
        if (!keys.SequenceEqual(_renderedKeys))
        {
            _renderedKeys = keys;
            _messages.Children.Clear();
            if (!string.IsNullOrEmpty(threadPath) && Stream is not null)
                foreach (var id in ids)
                {
                    var bubble = new ThreadMessageBubbleControl().WithNodePath($"{threadPath}/{id}").WithThreadPath(threadPath);
                    _messages.Children.Add(Renderer.RenderControl(bubble, Stream, Area));
                }
            foreach (var text in pending)
                _messages.Children.Add(PendingBubble(text));
            MainThread.BeginInvokeOnMainThread(() => _scroll.ScrollToAsync(_messages, ScrollToPosition.End, animated: false));
        }
        _status.Text = status ?? "";
        _status.IsVisible = executing && !string.IsNullOrEmpty(status);
    }

    private static View PendingBubble(string text) => new Border
    {
        Padding = new Thickness(10, 6),
        Margin = new Thickness(40, 2, 0, 2),
        HorizontalOptions = LayoutOptions.End,
        BackgroundColor = Color.FromArgb("#2A4A8C"),  // dimmer blue → "queued"
        StrokeThickness = 0,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
        Content = new Label { Text = text, TextColor = Colors.White, LineBreakMode = LineBreakMode.WordWrap },
    };

    private void Submit()
    {
        var text = _composer.Text?.Trim();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(_threadPath) || Stream is null) return;
        // Canonical submission surface — writes the thread node via GetMeshNodeStream(path).Update(...).
        Stream.Hub.SubmitMessage(_threadPath, text);
        _composer.Text = "";
    }

    private JsonElement? ToJsonElement(object? content)
    {
        if (content is null || Stream is null) return null;
        try
        {
            return content is JsonElement je ? je : JsonSerializer.SerializeToElement(content, Stream.Hub.JsonSerializerOptions);
        }
        catch { return null; }
    }
}
