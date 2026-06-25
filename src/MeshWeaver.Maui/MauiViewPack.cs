using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
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

        // Tie the area subscription to the host's Loaded/Unloaded lifecycle, re-subscribing each time it
        // re-enters the visual tree. MAUI fires Unloaded SPURIOUSLY during nested-layout/handler changes
        // (e.g. when the host sits inside the shell's content frame, several ContentViews deep) — disposing
        // on Unloaded ALONE killed the subscription before the area's first control ever arrived, leaving a
        // blank frame. GetControlStream replays the current control to a fresh subscriber, so re-subscribing
        // on (re)Load restores the content. An immediate subscribe covers hosts that render before Loaded.
        // Subscribe ONCE and stay subscribed — GetControlStream delivers an area's generator-produced
        // control on a later Full frame, so a subscription torn down on a spurious Unloaded (the old churn)
        // missed it. Dispose only when the host is finally removed AND not re-added (deferred check).
        sub = stream.GetControlStream(area)
            // Don't spin forever: if an area's control never arrives, surface WHY (timeout) instead of an
            // eternal spinner — so an unresolvable nested/remote area is diagnosable, not a mystery.
            .Timeout(TimeSpan.FromSeconds(15))
            .Subscribe(
                ctrl => MainThread.BeginInvokeOnMainThread(() =>
                    host.Content = ctrl is UiControl c ? RenderControl(c, stream, area) : null),
                ex => MainThread.BeginInvokeOnMainThread(() =>
                    host.Content = new Label
                    {
                        Text = $"⚠ area '{area}' didn't resolve — {ex.GetType().Name}",
                        TextColor = Colors.OrangeRed, FontSize = 11, Margin = new Thickness(8),
                        LineBreakMode = LineBreakMode.WordWrap,
                    }));
        host.Unloaded += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (host.Parent is null) { sub?.Dispose(); sub = null; }   // only if it didn't get re-parented
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
                .AddSingleton<IMauiControlRenderer, MauiControlRenderer>());

    private static MauiViewRegistry BuildRegistry() => new MauiViewRegistry()
        // Wave 1 — leaves (containers resolve to ContainerView via the IContainerControl fallback).
        .Register<LabelControl, LabelView>()
        .Register<ButtonControl, ButtonView>()
        .Register<HtmlControl, HtmlView>()
        .Register<MarkdownControl, MarkdownView>()
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
        .Register<ComboboxControl, ComboboxView>()
        .Register<ListboxControl, ListboxView>()
        // Tabular data.
        .Register<DataGridControl, DataGridView>()
        // Wave 2 — layout: a real grid + tabs (other containers fall back to ContainerView).
        .Register<LayoutGridControl, LayoutGridView>()
        .Register<TabsControl, TabsView>()
        // Wave 2 — query-driven: mesh node picker + catalog search.
        .Register<MeshNodePickerControl, MeshNodePickerView>()
        .Register<MeshSearchControl, MeshSearchView>()
        // Embedded remote area (e.g. the home page's bottom chat composer) → the existing LayoutAreaView.
        .Register<LayoutAreaControl, LayoutAreaControlView>()
        // Wave 2 — nav + badges.
        .Register<BadgeControl, BadgeView>()
        .Register<NavLinkControl, NavLinkView>()
        .Register<MenuItemControl, MenuItemView>();
}

// ---- Wave 1 control views -------------------------------------------------------------------------

/// <summary>Any container (Stack/LayoutGrid/Layout/Toolbar/…) → a MAUI stack of its child areas.</summary>
public sealed class ContainerView : MauiView
{
    protected override View CreateView()
    {
        // Honor StackControl orientation — a horizontal Stack (e.g. a tab bar / button row) must lay its
        // children left-to-right, not stacked vertically. Default + non-Stack containers stay vertical.
        var horizontal = Control is StackControl stack && IsHorizontal(stack.Skin);
        Microsoft.Maui.Controls.Layout layout = horizontal
            ? new HorizontalStackLayout { Spacing = 8 }
            : new VerticalStackLayout { Spacing = 8 };
        if (Stream is not null && Control is IContainerControl container)
            foreach (var named in container.Areas)
                layout.Children.Add(Renderer.RenderArea(Stream, named.Area.ToString()!));
        return layout;
    }

    // Orientation rides the skin as object? (an enum at author time, a JSON string after stream round-trip).
    private static bool IsHorizontal(LayoutStackSkin? skin) =>
        skin?.Orientation?.ToString()?.Contains("Horizontal", StringComparison.OrdinalIgnoreCase) == true;
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
    protected override View CreateView() => _button = new Button();
    protected override void Bind() => Bind<object>(Model.Data, v => _button.Text = v?.ToString() ?? "");
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
    protected override View CreateView() =>
        _web = new WebView { HeightRequest = 120, BackgroundColor = Colors.Transparent };
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
        return _web = new WebView { HeightRequest = 240, BackgroundColor = Colors.Transparent };
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

/// <summary>Navigation link → a MAUI <see cref="Button"/> (title; href navigation is a later wave).</summary>
public sealed class NavLinkView : MauiView<NavLinkControl>
{
    private Button _button = null!;
    protected override View CreateView() => _button = new Button { HorizontalOptions = LayoutOptions.Start };
    protected override void Bind() => Bind<object>(Model.Title, v => _button.Text = v?.ToString() ?? "");
}

/// <summary>Menu item → a MAUI <see cref="Label"/> (title).</summary>
public sealed class MenuItemView : MauiView<MenuItemControl>
{
    private Label _label = null!;
    protected override View CreateView() => _label = new Label();
    protected override void Bind() => Bind<object>(Model.Title, v => _label.Text = v?.ToString() ?? "");
}

/// <summary>Icon glyph/text → MAUI <see cref="Label"/>.</summary>
public sealed class IconView : MauiView<IconControl>
{
    private Label _label = null!;
    protected override View CreateView() => _label = new Label();
    protected override void Bind() => Bind<object>(Model.Data, v => _label.Text = v?.ToString() ?? "");
}

/// <summary>Tabular data → a header + rows (read-only this wave; sorting/virtualization later).</summary>
public sealed class DataGridView : MauiView<DataGridControl>
{
    private VerticalStackLayout _root = null!;
    private readonly List<PropertyColumnControl> _columns = new();

    protected override View CreateView()
    {
        _columns.AddRange(Model.Columns.OfType<PropertyColumnControl>());
        _root = new VerticalStackLayout { Spacing = 4 };
        _root.Children.Add(Row(_columns.Select(c => c.Title?.ToString() ?? c.Property ?? "").ToArray(), bold: true));
        return _root;
    }

    protected override void Bind() => Bind<JsonElement>(Model.Data, RenderRows);

    private void RenderRows(JsonElement rows)
    {
        while (_root.Children.Count > 1) _root.Children.RemoveAt(1);   // keep the header row
        if (rows.ValueKind != JsonValueKind.Array) return;
        foreach (var row in rows.EnumerateArray())
            _root.Children.Add(Row(_columns.Select(c => CellText(row, c.Property)).ToArray(), bold: false));
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
    private List<Option> _options = new();

    protected override View CreateView()
    {
        _picker = new Picker();
        _options = (Model.Options as IEnumerable<Option>)?.ToList() ?? new();
        foreach (var o in _options) _picker.Items.Add(o.Text);
        _picker.SelectedIndexChanged += (_, _) =>
        {
            if (_picker.SelectedIndex >= 0 && _picker.SelectedIndex < _options.Count)
                Write(Model.Data, _options[_picker.SelectedIndex].GetItem());
        };
        return _picker;
    }

    protected override void Bind() => BindValue<object>(Model.Data, v =>
    {
        var idx = _options.FindIndex(o => Equals(o.GetItem()?.ToString(), v?.ToString()));
        if (idx >= 0) _picker.SelectedIndex = idx;
    });
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

/// <summary>Combobox → MAUI <see cref="Picker"/> (two-way; native Picker has no free-type filter — a
/// later Monaco/autocomplete wave can add that).</summary>
public sealed class ComboboxView : FormMauiView<ComboboxControl>
{
    private Picker _picker = null!;
    private List<Option> _options = new();
    protected override View CreateView()
    {
        _picker = new Picker();
        _options = (Model.Options as IEnumerable<Option>)?.ToList() ?? new();
        foreach (var o in _options) _picker.Items.Add(o.Text);
        _picker.SelectedIndexChanged += (_, _) =>
        {
            if (_picker.SelectedIndex >= 0 && _picker.SelectedIndex < _options.Count)
                Write(Model.Data, _options[_picker.SelectedIndex].GetItem());
        };
        return _picker;
    }
    protected override void Bind() => BindValue<object>(Model.Data, v =>
    {
        var idx = _options.FindIndex(o => Equals(o.GetItem()?.ToString(), v?.ToString()));
        if (idx >= 0) _picker.SelectedIndex = idx;
    });
}

/// <summary>Listbox → MAUI <see cref="Picker"/> (two-way single-select this wave).</summary>
public sealed class ListboxView : FormMauiView<ListboxControl>
{
    private Picker _picker = null!;
    private List<Option> _options = new();
    protected override View CreateView()
    {
        _picker = new Picker();
        _options = (Model.Options as IEnumerable<Option>)?.ToList() ?? new();
        foreach (var o in _options) _picker.Items.Add(o.Text);
        _picker.SelectedIndexChanged += (_, _) =>
        {
            if (_picker.SelectedIndex >= 0 && _picker.SelectedIndex < _options.Count)
                Write(Model.Data, _options[_picker.SelectedIndex].GetItem());
        };
        return _picker;
    }
    protected override void Bind() => BindValue<object>(Model.Data, v =>
    {
        var idx = _options.FindIndex(o => Equals(o.GetItem()?.ToString(), v?.ToString()));
        if (idx >= 0) _picker.SelectedIndex = idx;
    });
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
