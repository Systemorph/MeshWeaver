using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
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
        var host = new ContentView();
        var sub = stream.GetControlStream(area)
            .Subscribe(ctrl => MainThread.BeginInvokeOnMainThread(() =>
                host.Content = ctrl is UiControl c ? RenderControl(c, stream, area) : null));
        host.Unloaded += (_, _) => sub.Dispose();   // tear the subscription down with the view
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
/// The public host: renders a mesh layout area (a control tree) as native MAUI. Subscribes to the LOCAL
/// workspace's area stream and renders reactively. (Remote areas via <c>GetRemoteStream</c> come next wave.)
/// </summary>
public sealed class LayoutAreaView : ContentView
{
    public LayoutAreaView(IWorkspace workspace, LayoutAreaReference reference, IMauiControlRenderer renderer)
    {
        var stream = workspace.GetStream(reference)!.Reduce(new JsonPointerReference("/"))!;
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
        .Register<NamedAreaControl, NamedAreaView>();
}

// ---- Wave 1 control views -------------------------------------------------------------------------

/// <summary>Any container (Stack/LayoutGrid/Layout/Toolbar/…) → a MAUI stack of its child areas.</summary>
public sealed class ContainerView : MauiView
{
    protected override View CreateView()
    {
        var layout = new VerticalStackLayout { Spacing = 8 };
        if (Stream is not null && Control is IContainerControl container)
            foreach (var named in container.Areas)
                layout.Children.Add(Renderer.RenderArea(Stream, named.Area.ToString()!));
        return layout;
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
    protected override View CreateView() => _button = new Button();
    protected override void Bind() => Bind<object>(Model.Data, v => _button.Text = v?.ToString() ?? "");
}

/// <summary>Pre-rendered HTML/text → MAUI <see cref="Label"/> (rich HTML rendering is a later wave). </summary>
public sealed class HtmlView : MauiView<HtmlControl>
{
    private Label _label = null!;
    protected override View CreateView() => _label = new Label();
    protected override void Bind() => Bind<object>(Model.Data, v => _label.Text = v?.ToString() ?? "");
}

/// <summary>Markdown → MAUI <see cref="Label"/> (raw this wave; Markdig formatting is a later wave).</summary>
public sealed class MarkdownView : MauiView<MarkdownControl>
{
    private Label _label = null!;
    protected override View CreateView() => _label = new Label();
    protected override void Bind() => Bind<object>(Model.Markdown, v => _label.Text = v?.ToString() ?? "");
}

/// <summary>Icon glyph/text → MAUI <see cref="Label"/>.</summary>
public sealed class IconView : MauiView<IconControl>
{
    private Label _label = null!;
    protected override View CreateView() => _label = new Label();
    protected override void Bind() => Bind<object>(Model.Data, v => _label.Text = v?.ToString() ?? "");
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
