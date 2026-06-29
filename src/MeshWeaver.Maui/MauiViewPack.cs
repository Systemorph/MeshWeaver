using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// can show + select. Handles both a typed <see cref="Option"/> list AND — the case the old code
    /// missed — a <see cref="JsonElement"/> array (Options arrive serialized after the layout-stream
    /// round-trip, so <c>as IEnumerable&lt;Option&gt;</c> was null → no options rendered). Value is the
    /// item's string form (selection-match + write-back); covers the common string/enum-valued option.
    /// </summary>
    protected static List<(string Text, string? Value)> CoerceOptions(object? options)
    {
        var result = new List<(string Text, string? Value)>();
        switch (options)
        {
            case IEnumerable<Option> typed:
                foreach (var o in typed) result.Add((o.Text, o.GetItem()?.ToString()));
                break;
            case JsonElement { ValueKind: JsonValueKind.Array } arr:
                foreach (var el in arr.EnumerateArray())
                {
                    var text = el.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString() ?? ""
                        : el.ToString();
                    var val = el.TryGetProperty("item", out var it) ? JsonScalar(it)
                        : el.TryGetProperty("itemString", out var its) ? its.GetString()
                        : text;
                    result.Add((text, val));
                }
                break;
        }
        return result;
    }

    private static string? JsonScalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Null => null,
        _ => e.GetRawText()
    };
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
        // Embedded remote area (e.g. the home page's bottom chat composer) → the existing LayoutAreaView.
        .Register<LayoutAreaControl, LayoutAreaControlView>()
        // Wave 2 — nav + badges.
        .Register<BadgeControl, BadgeView>()
        .Register<NavLinkControl, NavLinkView>()
        .Register<MenuItemControl, MenuItemView>()
        // Phase 2 — agent-backed chat: a single thread message bubble (streaming text + exec status).
        .Register<ThreadMessageBubbleControl, ThreadMessageBubbleView>();
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

    // A WebView chunk in MarkdownView's dark/sans shell, auto-sized to its content (JS scrollHeight).
    private static View NewHtmlChunk(string bodyHtml)
    {
        var web = new WebView { HeightRequest = 80, BackgroundColor = Colors.Transparent };
        web.Source = MauiHtmlDocument.ForBody(bodyHtml);
        web.Navigated += async (_, _) =>
        {
            try
            {
                var h = await web.EvaluateJavaScriptAsync("document.body.scrollHeight");
                if (double.TryParse(h, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var px) && px > 0)
                    MainThread.BeginInvokeOnMainThread(() => web.HeightRequest = px + 8);
            }
            catch { /* keep the fallback height */ }
        };
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
        Bind<object>(Model.Data, v => _host.Content = Build((MarkdownViewLogic.CoerceString(v) ?? "").Trim()));

    private static View Build(string icon)
    {
        if (string.IsNullOrEmpty(icon)) return new ContentView();

        var isSvg = icon.Contains("<svg", StringComparison.OrdinalIgnoreCase)
                    || icon.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase)
                    || (icon.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        && icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase));
        if (isSvg)
            return new WebView
            {
                WidthRequest = Size, HeightRequest = Size, BackgroundColor = Colors.Transparent,
                Source = new HtmlWebViewSource { Html = SvgHtml(icon) },
            };

        // Raster image (png/jpg/…) by URL or data-URI → native Image.
        if (icon.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || icon.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || icon.StartsWith("/", StringComparison.Ordinal))
            return new Image { Source = icon, WidthRequest = Size, HeightRequest = Size, Aspect = Aspect.AspectFit };

        // Glyph / emoji / (Fluent) icon name — no native icon set, so show as text (unchanged behaviour).
        return new Label { Text = icon, FontSize = 16, VerticalOptions = LayoutOptions.Center };
    }

    // $$"""…""" raw interpolation: single { } are LITERAL CSS braces; {{…}} are interpolations.
    private static string SvgHtml(string icon)
    {
        var body = icon.Contains("<svg", StringComparison.OrdinalIgnoreCase)
            ? icon
            : $"<img src=\"{icon}\" style=\"width:{Size}px;height:{Size}px\"/>";
        return $$"""
            <!DOCTYPE html><html><head><meta charset="utf-8">
            <style>html,body{margin:0;padding:0;background:transparent;overflow:hidden;color:#e0e0e0}
            svg{width:{{Size}}px;height:{{Size}}px;display:block;fill:currentColor}
            img{display:block}</style></head><body>{{body}}</body></html>
            """;
    }
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
    private Label _body = null!;
    private ActivityIndicator _spinner = null!;
    private Label _status = null!;
    private Label _footer = null!;
    private HorizontalStackLayout _statusRow = null!;

    protected override View CreateView()
    {
        var isUser = string.Equals(Model.Role, "user", StringComparison.OrdinalIgnoreCase);

        var author = new Label
        {
            Text = string.IsNullOrWhiteSpace(Model.AuthorName) ? (isUser ? "You" : "Assistant") : Model.AuthorName,
            FontSize = 11, FontAttributes = FontAttributes.Bold,
            TextColor = isUser ? Colors.White : Color.FromArgb("#C0C0C0"),
        };
        _body = new Label { TextColor = Colors.White, LineBreakMode = LineBreakMode.WordWrap };

        _spinner = new ActivityIndicator { IsVisible = false, IsRunning = false, Color = Colors.White, HeightRequest = 14, WidthRequest = 14 };
        _status = new Label { FontSize = 11, TextColor = Color.FromArgb("#C0C0C0") };
        _statusRow = new HorizontalStackLayout { Spacing = 6, IsVisible = false, Children = { _spinner, _status } };

        _footer = new Label { FontSize = 10, TextColor = Color.FromArgb("#9A9A9A") };

        var content = new VerticalStackLayout { Spacing = 4, Children = { author, _body, _statusRow, _footer } };

        return new Border
        {
            Padding = new Thickness(10, 6),
            Margin = new Thickness(isUser ? 40 : 0, 2, isUser ? 0 : 40, 2),
            HorizontalOptions = isUser ? LayoutOptions.End : LayoutOptions.Start,
            BackgroundColor = isUser ? Colors.RoyalBlue : Color.FromArgb("#3A3A3C"),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Content = content,
        };
    }

    protected override void Bind()
    {
        Bind<string>(Model.Text, v => _body.Text = v ?? "");
        Bind<bool>(Model.IsExecuting, executing =>
        {
            _statusRow.IsVisible = executing;
            _spinner.IsVisible = executing;
            _spinner.IsRunning = executing;
        }, defaultValue: false);
        Bind<string>(Model.ExecutionStatus, v => _status.Text = v ?? "");
        _footer.Text = BuildFooter();
        _footer.IsVisible = !string.IsNullOrEmpty(_footer.Text);
    }

    private string BuildFooter()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Model.ModelName)) parts.Add(Model.ModelName!);
        if (Model.Timestamp is { } ts) parts.Add(ts.ToLocalTime().ToString("t"));
        return string.Join(" · ", parts);
    }
}
