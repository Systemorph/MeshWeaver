using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor;

/// <summary>
/// Base Blazor view component that pairs a <typeparamref name="TViewModel"/> control model with its
/// concrete <typeparamref name="TView"/> Razor component. Manages reactive data bindings, cascading
/// parameters, stream subscriptions, and lifecycle disposal for all layout-area views.
/// </summary>
/// <typeparam name="TViewModel">The <c>IUiControl</c> view-model type this component renders.</typeparam>
/// <typeparam name="TView">The concrete Blazor component type that inherits this base class.</typeparam>
public class BlazorView<TViewModel, TView> : ComponentBase, IAsyncDisposable
    where TViewModel : IUiControl
    where TView : BlazorView<TViewModel, TView>
{
    /// <summary>Logger scoped to the concrete view type, used for debug/trace binding lifecycle messages.</summary>
    [Inject] protected ILogger<TView> Logger { get; set; } = null!;
    /// <summary>Portal application root, providing the <c>Hub</c> and other portal-wide services.</summary>
    [Inject] protected PortalApplication PortalApplication { get; set; } = null!;
    /// <summary>JavaScript interop runtime, used for theme detection and other JS bridge calls.</summary>
    [Inject] protected IJSRuntime JSRuntime { get; set; } = null!;
    /// <summary>
    /// The circuit's <c>AccessService</c>. Set by <c>CircuitAccessHandler</c> to the clicking user's
    /// identity so that user-driven messages such as <c>ClickedEvent</c> carry the correct
    /// <c>AccessContext</c> across the sync-stream boundary.
    /// </summary>
    // The circuit's AccessService — CircuitAccessHandler sets the clicking user's identity on it. Used to
    // stamp user-driven messages (ClickedEvent) so they don't lose identity crossing the sync-stream
    // boundary (Stream.Hub is the sync hub, whose AccessService has no user context).
    [Inject] protected AccessService AccessService { get; set; } = null!;
    /// <summary>
    /// Circuit-scoped service provider. Used to resolve optional services such as
    /// <c>PortalErrorSink</c> that non-portal hosts (e.g. MAUI) do not register.
    /// </summary>
    // The circuit's service provider — used to resolve the optional PortalErrorSink (modal surface) for
    // SurfaceError. Resolved lazily (GetService, not [Inject]) because non-portal hosts (e.g. the MAUI
    // client) don't register the sink.
    [Inject] protected IServiceProvider Services { get; set; } = null!;
    /// <summary>The portal's message hub, shortcut via <c>PortalApplication.Hub</c>.</summary>
    protected IMessageHub Hub => PortalApplication.Hub;
    /// <summary>The control view-model that drives this component's rendered output.</summary>
    [Parameter] public required TViewModel ViewModel { get; set; } 

    /// <summary>Synchronization stream supplying data-bound values for JSON-pointer references in this area.</summary>
    [Parameter] public ISynchronizationStream<JsonElement>? Stream { get; set; } 
    /// <summary>The layout-area path identifier for this component within the page hierarchy.</summary>
    [Parameter] public string Area { get; set; } = null!;

    /// <summary>Optional context object cascaded down the component tree, providing ambient data for <c>ContextProperty</c> bindings.</summary>
    [CascadingParameter(Name = "Context")] public object? Context { get; set; }

    /// <summary>Current Fluent UI design theme mode (light/dark/system), cascaded from the root layout.</summary>
    [CascadingParameter(Name = "ThemeMode")]
    public DesignThemeModes Mode { get; set; }

    /// <summary>
    /// The data-context path cascaded from a parent container. JSON-pointer references without a leading
    /// <c>/</c> are resolved relative to this path.
    /// </summary>
    [CascadingParameter(Name = nameof(DataContext))]
    public string? DataContext { get; set; }

    /// <summary>
    /// Inline model parameter cascaded by container views. When set, pointer references without a
    /// leading <c>/</c> are resolved against this model rather than the synchronization stream.
    /// </summary>
    [CascadingParameter(Name = nameof(Model))]
    public ModelParameter<JsonElement>? Model { get; set; }

    /// <summary>Inline CSS style string bound from <c>ViewModel.Style</c>; applied to the root element.</summary>
    protected string? Style { get; set; }

    /// <summary>Tears down previous data bindings and rebuilds them after every Blazor parameter update.</summary>
    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        BindDataAfterParameterReset();
    }

    /// <summary>
    /// Called by <c>OnParametersSet</c> to dispose stale bindings and invoke <c>BindData</c> for the
    /// new parameter values.
    /// </summary>
    protected virtual void BindDataAfterParameterReset()
    {
        Logger.LogDebug("Preparing data bindings for area {Area}", Area);
        DisposeBindings();
        BindData();
        Logger.LogDebug("Finished data bindings for area {Area}", Area);
    }


    /// <summary>CSS class string bound from <c>ViewModel.Class</c>; applied to the root element.</summary>
    protected string? Class { get; set; }
    /// <summary>HTML element id bound from <c>ViewModel.Id</c>.</summary>
    protected string? Id { get; set; }

    /// <summary>Component-lifetime disposables released in <c>DisposeAsync</c>, in addition to data-binding subscriptions.</summary>
    protected List<IDisposable> Disposables { get; } = new();

    private bool _viewDisposed;

    /// <summary>True after <see cref="DisposeAsync"/> has been entered. Subscription callbacks
    /// can check this to avoid invoking <c>StateHasChanged</c> on a dead renderer.</summary>
    protected bool IsViewDisposed => _viewDisposed;

    /// <summary>
    /// Sets <c>IsViewDisposed</c>, disposes all data-binding subscriptions, and releases all
    /// <c>Disposables</c> to cleanly tear down the component.
    /// </summary>
    public virtual ValueTask DisposeAsync()
    {
        // Set the flag BEFORE disposing subscriptions so any in-flight callbacks
        // queued by Subscribe(...) onto the synchronization context can short-circuit
        // on IsViewDisposed instead of touching a torn-down renderer.
        _viewDisposed = true;
        Logger.LogDebug("Disposing area {Area}", Area);
        DisposeBindings();
        foreach (var d in Disposables)
            d.Dispose();
        Disposables.Clear();
        return default;
    }

    /// <summary>Registers a data-binding subscription to be disposed when bindings are reset or the component is disposed.</summary>
    /// <param name="binding">The subscription handle returned by a reactive binding call.</param>
    protected void AddBinding(IDisposable binding)
    {
        bindings.Add(binding);
    }

    /// <summary>
    /// Resolves <paramref name="value"/> — which may be a <c>JsonPointerReference</c>, a
    /// <c>ContextProperty</c>, or a plain value — and sets the target property on this component.
    /// For pointer and context references a live subscription is registered so the property updates
    /// reactively whenever the underlying stream or data context emits a new value.
    /// </summary>
    /// <typeparam name="T">The type of the target property.</typeparam>
    /// <param name="value">The source value or reference to bind.</param>
    /// <param name="propertySelector">An expression selecting the target property or field on this view.</param>
    /// <param name="conversion">Optional conversion function from the raw JSON-deserialized value to <typeparamref name="T"/>.</param>
    /// <param name="defaultValue">Value assigned when the source is null or the binding faults.</param>
    protected void DataBind<T>(
        object? value, 
        Expression<Func<TView, T?>> propertySelector, 
        Func<object?,T?, T?>? conversion = null,
        T defaultValue = default!)
    {
        try
        {
            var expr = propertySelector.Body as MemberExpression;
            Action<object?> setter = expr?.Member is PropertyInfo pi 
                ? o => pi.SetValue(this,o) 
                : expr?.Member is FieldInfo fi 
                    ? o => fi.SetValue(this, o) 
                    : throw new ArgumentException(
                        "Expression provided must point to a property or field.", 
                        nameof(propertySelector)
                        );

            if (value is JsonPointerReference reference)
            {
                try
                {
                    // Node-bound DataContext: read the field straight off the node stream (the
                    // process-wide IMeshNodeStreamCache) instead of a /data replica. ONE source of
                    // truth — see LayoutAreaReference.MeshNodePrefix and Doc/GUI/DataBinding.
                    if (LayoutAreaReference.TryParseMeshNodeDataContext(DataContext) is { } meshNode
                        && !reference.Pointer.StartsWith('/'))
                    {
                        bindings.Add(MeshNodeBindingExtensions
                            .Bind(Hub, meshNode.NodePath, meshNode.BindContent, meshNode.SubPath, reference)
                            .Subscribe(v =>
                            {
                                if (_viewDisposed) return;
                                try
                                {
                                    InvokeAsync(() =>
                                    {
                                        if (_viewDisposed) return;
                                        try
                                        {
                                            setter(Hub.ConvertSingle(v, conversion, defaultValue));
                                            RequestStateChange();
                                        }
                                        catch (ObjectDisposedException) { /* renderer gone */ }
                                        catch (Exception ex)
                                        {
                                            Logger.LogError(ex, "Error setting node-bound property value in Area {area}", Area);
                                        }
                                    });
                                }
                                catch (ObjectDisposedException) { /* renderer gone */ }
                                catch (Exception ex)
                                {
                                    Logger.LogError(ex, "Error scheduling node-bound property update in Area {area}", Area);
                                }
                            },
                            ex => SurfaceError(ex,
                                $"Loading '{reference.Pointer}'" + (string.IsNullOrEmpty(Area) ? "" : $" in area '{Area}'"))));
                    }
                    else if (Model is not null && !reference.Pointer.StartsWith('/'))
                    {
                        var convertedValue = Hub.ConvertSingle(Model.GetValueFromModel(reference), conversion, defaultValue);
                        setter(convertedValue);
                    }
                    else if(Stream is not null)
                    {
                        bindings.Add(Stream.DataBind(reference, DataContext, conversion, defaultValue)
                            .Subscribe(v =>
                                {
                                    if (_viewDisposed) return;
                                    try
                                    {
                                        Logger.LogTrace("Binding property in Area {area}", Area);
                                        InvokeAsync(() =>
                                        {
                                            // Re-check after dispatch — the renderer may have
                                            // been torn down between Subscribe.OnNext and the
                                            // synchronization-context callback firing.
                                            if (_viewDisposed) return;
                                            try
                                            {
                                                setter(v);
                                                RequestStateChange();
                                            }
                                            catch (ObjectDisposedException) { /* renderer gone */ }
                                            catch (Exception ex)
                                            {
                                                Logger.LogError(ex, "Error setting bound property value in Area {area}", Area);
                                            }
                                        });
                                    }
                                    catch (ObjectDisposedException) { /* renderer gone */ }
                                    catch (Exception ex)
                                    {
                                        Logger.LogError(ex, "Error scheduling bound property update in Area {area}", Area);
                                    }
                                },
                                // 🚨 A bound stream that FAULTS must be SURFACED, never swallowed: a
                                // Subscribe with no onError leaves the fault unobserved, the property
                                // never gets a value, and the control spins forever — the "gui is just
                                // hanging" symptom (atioz 2026-06-21, when the data stream OnError'd from
                                // the AccessContext storm). Mirror the node-bound branch above: log it,
                                // then render the DEFAULT value on the UI thread so the control draws
                                // (empty/zeroed) instead of hanging. ObjectDisposedException is a benign
                                // teardown artifact (navigation / component swap) — Debug, not surfaced.
                                ex =>
                                {
                                    if (_viewDisposed || ex is ObjectDisposedException)
                                    {
                                        Logger.LogDebug(ex, "Suppressed teardown error binding '{pointer}' in Area {area}", reference.Pointer, Area);
                                        return;
                                    }
                                    // SURFACE the fault to the user (modal + inline) — a silent blank is the
                                    // chat-disappear symptom the user flagged. SurfaceError logs at Error,
                                    // pops the PortalErrorModal, and sets SurfacedError for inline display;
                                    // it suppresses teardown (ObjectDisposedException) itself. We STILL render
                                    // the default below so the control draws instead of spinning.
                                    SurfaceError(ex,
                                        $"Loading '{reference.Pointer}'" + (string.IsNullOrEmpty(Area) ? "" : $" in area '{Area}'"));
                                    try
                                    {
                                        InvokeAsync(() =>
                                        {
                                            if (_viewDisposed) return;
                                            try
                                            {
                                                setter(Hub.ConvertSingle(null, conversion, defaultValue));
                                                RequestStateChange();
                                            }
                                            catch (ObjectDisposedException) { /* renderer gone */ }
                                            catch (Exception inner)
                                            {
                                                Logger.LogError(inner, "Error setting default after binding fault in Area {area}", Area);
                                            }
                                        });
                                    }
                                    catch (ObjectDisposedException) { /* renderer gone */ }
                                }
                            )
                        );
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error binding JsonPointerReference '{pointer}' in Area {area}", reference.Pointer, Area);
                    // Set default value on binding failure
                    setter(Hub.ConvertSingle(null, conversion, defaultValue));
                }
            }
            else if (value is ContextProperty contextProperty)
            {
                try
                {
                    var val = 
                        Context is JsonObject jo 
                            ? jo[contextProperty.Property]
                            : Context?.GetType().GetProperty(contextProperty.Property)?.GetValue(Context);
                    setter(Hub.ConvertSingle(val, conversion, defaultValue));
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error binding ContextProperty '{property}' in Area {area}", contextProperty.Property, Area);
                    // Set default value on binding failure
                    setter(Hub.ConvertSingle(null, conversion, defaultValue));
                }
            }
            else
            {
                try
                {
                    setter(Hub.ConvertSingle(value, conversion, defaultValue));
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error binding value '{value}' in Area {area}", value, Area);
                    // Set default value on binding failure
                    setter(Hub.ConvertSingle(null, conversion, defaultValue));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Critical error in DataBind for Area {area}", Area);
            // Attempt to set default value as last resort
            try
            {
                var expr = propertySelector.Body as MemberExpression;
                if (expr?.Member is PropertyInfo pi)
                {
                    pi.SetValue(this, defaultValue);
                }
                else if (expr?.Member is FieldInfo fi)
                {
                    fi.SetValue(this, defaultValue);
                }
            }
            catch (Exception innerEx)
            {
                Logger.LogError(innerEx, "Failed to set default value after DataBind error in Area {area}", Area);
            }
        }
    }

    /// <summary>Requests a Blazor re-render by calling <c>StateHasChanged</c>.</summary>
    protected void RequestStateChange()
    {
        StateHasChanged();
    }

    /// <summary>
    /// The most recent ASYNC/stream fault surfaced on this view (null = none). A component MAY render it
    /// inline (e.g. a small error banner). Render-time throws are handled separately by the DispatchView /
    /// LayoutAreaView <c>ErrorBoundary</c>; this is for Rx <c>OnError</c> faults, which an ErrorBoundary
    /// cannot see.
    /// </summary>
    protected Exception? SurfacedError { get; private set; }

    /// <summary>
    /// Canonical onError handler for view-component subscriptions. Surfaces an asynchronous/stream fault
    /// BOTH as a modal (via the circuit's <see cref="PortalErrorSink"/>, when registered) AND inline (sets
    /// <see cref="SurfacedError"/> + re-renders so a component can show it), and logs it. Benign teardown
    /// faults (<see cref="ObjectDisposedException"/> / post-dispose) are logged at Debug and not surfaced.
    /// <para>Use it INSTEAD of a no-arg <c>Subscribe(onNext)</c>: a subscription with no onError leaves the
    /// fault unobserved, the binding never updates, and the view spins forever — the "gui just hangs"
    /// symptom. Pass a short human context, e.g. <c>SurfaceError(ex, "Loading thread messages")</c>.</para>
    /// </summary>
    protected void SurfaceError(Exception ex, string context)
    {
        if (_viewDisposed || ex is ObjectDisposedException)
        {
            Logger.LogDebug(ex, "Suppressed teardown error: {Context} in Area {Area}", context, Area);
            return;
        }

        Logger.LogError(ex, "{Context} in Area {Area}", context, Area);

        // Modal surface — best-effort; the surfacing path must never throw back into the faulting
        // subscription (which runs on an Rx scheduler thread).
        try { Services.GetService<PortalErrorSink>()?.Report($"{context}: {ex.Message}"); }
        catch (Exception sinkEx) { Logger.LogDebug(sinkEx, "PortalErrorSink.Report threw while surfacing {Context}", context); }

        // Inline surface — set the field and re-render on the UI thread.
        try
        {
            InvokeAsync(() =>
            {
                if (_viewDisposed) return;
                SurfacedError = ex;
                RequestStateChange();
            });
        }
        catch (ObjectDisposedException) { /* renderer gone */ }
    }

    /// <summary>Returns a child area path by appending <paramref name="area"/> to the current <c>Area</c>.</summary>
    /// <param name="area">The child area segment to append.</param>
    /// <returns>The full child area path.</returns>
    protected string SubArea(string area)
        => $"{Area}/{area}";

    /// <summary>
    /// Writes <paramref name="value"/> back to the data source at the location identified by
    /// <paramref name="reference"/>. Routes to the mesh-node stream when the data context is a
    /// node reference, otherwise writes through the synchronization stream.
    /// </summary>
    /// <param name="value">The new value to write.</param>
    /// <param name="reference">The JSON-pointer reference identifying the field to update.</param>
    protected virtual void UpdatePointer(object? value, JsonPointerReference reference)
    {
        // Node-bound DataContext: write the edited field straight back to the node stream
        // (per-field read-modify-write through IMeshNodeStreamCache). No /data replica, no
        // server-side save subscription — see LayoutAreaReference.MeshNodePrefix.
        if (LayoutAreaReference.TryParseMeshNodeDataContext(DataContext) is { } meshNode
            && !reference.Pointer.StartsWith('/'))
        {
            MeshNodeBindingExtensions.Write(Hub, Logger, meshNode.NodePath, meshNode.BindContent, meshNode.SubPath, reference, value,
                onError: ex => SurfaceError(ex, $"Saving '{reference.Pointer}'"));
            return;
        }

        if(Stream is null)
            throw new InvalidOperationException("Stream must be set before updating pointers.");
        Stream.UpdatePointer(value, DataContext ?? "/", reference, Model);
    }


    /// <summary>
    /// Binds the view-model's <c>Id</c>, <c>Class</c>, and <c>Style</c> properties to the corresponding
    /// protected fields on this component. Override to add control-specific bindings.
    /// </summary>
    protected virtual void BindData()
    {
        // ViewModel is declared `required` but Blazor's parameter pipeline can
        // still feed null through transient binding races — most reliably during
        // thread-launch / chat-side-panel re-render where the upstream Stream is
        // being torn down and a new ViewModel hasn't landed yet. Without this
        // guard the user sees a NullReferenceException at the .Id access below.
        if (ViewModel is null) return;

        DataBind(ViewModel.Id, x => x.Id);
        DataBind(ViewModel.Class, x => x.Class);
        DataBind(ViewModel.Style, x => x.Style);
    }
    private readonly List<IDisposable> bindings = new();

    /// <summary>Disposes all active data-binding subscriptions registered via <c>AddBinding</c> and <c>DataBind</c>.</summary>
    protected virtual void DisposeBindings()
    {
        foreach (var d in bindings)
            d.Dispose();
        bindings.Clear();
    }

    /// <summary>
    /// Posts a <c>ClickedEvent</c> for the current area to the stream owner, stamping the circuit
    /// user's <c>AccessContext</c> so the event is not denied by the hub's access gate.
    /// </summary>
    protected virtual void OnClick()
    {
        if(Stream is null)
            throw new InvalidOperationException("Stream must be set before sending click events.");
        // ClickedEvent is a USER-driven message — it must carry the clicking user's identity. Stream.Hub
        // is the sync hub, whose AccessService has no user context (the circuit's AccessService holds it),
        // so a bare Post lands context-less → PostPipeline fails closed → the click's downstream write is
        // denied and the synced area blanks. Stamp the circuit user's AccessContext explicitly; it then
        // travels with the delivery to the owning hub (Phase 2 rule 1 keeps an explicit context).
        // See Doc/Architecture/AccessContextPropagation.md → "sync stream protocol vs user-data".
        var userContext = AccessService?.Context ?? AccessService?.CircuitContext;
        Stream.Hub.Post(new ClickedEvent(Area, Stream.StreamId), o =>
            userContext is not null
                ? o.WithTarget(Stream.Owner).WithAccessContext(userContext)
                : o.WithTarget(Stream.Owner));
    }

    // ─── Circuit-user re-establishment for DEFERRED mesh ops ───────────────────────
    // Blazor's CircuitAccessHandler sets the user on the circuit AccessService only for the duration
    // of an inbound activity, then NULLS it in finally. So any mesh read/write a view defers behind an
    // Rx hop or InvokeAsync (a picker/skill query that subscribes on the synced-query scheduler, a
    // .Update that runs after the activity returned) reads a NULL ambient AccessContext → the owning
    // hub's PostPipeline fails closed → owner RLS denies ("Access denied" / empty registry). The DURABLE
    // per-circuit identity lives on ICircuitContextAccessor.UserContext (cleared only on circuit close),
    // so it survives the hop. These three helpers are the ONE place to re-establish it; every view uses
    // them instead of re-deriving the user per call site. See AccessContextPropagation.md.

    /// <summary>
    /// The durable circuit user, usable from deferred (post-Rx-hop) callbacks. Prefers
    /// <see cref="ICircuitContextAccessor.UserContext"/> (survives every hop), then the live
    /// <see cref="AccessService.CircuitContext"/> / <see cref="AccessService.Context"/> AsyncLocals for
    /// non-deferred callers. A leaked <c>system-security</c> / hub-shaped principal is rejected — never a
    /// real user. Returns <see langword="null"/> outside a circuit (SSR / prerender) or when no user is set.
    /// </summary>
    protected AccessContext? ResolveCircuitUser()
    {
        var accessor = Services?.GetService<ICircuitContextAccessor>();
        foreach (var candidate in new[] { accessor?.UserContext, AccessService?.CircuitContext, AccessService?.Context })
            if (candidate is not null
                && !string.IsNullOrEmpty(candidate.ObjectId)
                && candidate.ObjectId != MeshWeaver.Mesh.Security.WellKnownUsers.System
                && !AccessService.LooksLikeHubPrincipal(candidate.ObjectId))
                return candidate;
        return null;
    }

    /// <summary>
    /// Runs <paramref name="source"/> with the durable circuit user (<see cref="ResolveCircuitUser"/>)
    /// re-established on the HUB <see cref="AccessService"/> — the SAME instance <c>Hub.GetQuery</c> /
    /// <c>Hub.GetMeshNodeStream(path).Update()</c> read to attribute reads/writes. The identity is
    /// resolved and switched at SUBSCRIBE (inside the <c>Observable.Using</c> resource factory) and held
    /// for the whole subscription, so the synced-query / IIoPool hops the source fans out across all
    /// carry it. Use to wrap any mesh READ a view subscribes after an Rx hop (picker, skill, completions).
    /// </summary>
    protected IObservable<T> RunUnderCircuitUser<T>(IObservable<T> source)
    {
        var hubAccess = Hub.ServiceProvider.GetService<AccessService>();
        return Observable.Using(
            () =>
            {
                var user = ResolveCircuitUser();
                return user is not null && hubAccess is not null
                    ? hubAccess.SwitchAccessContext(user)
                    : (IDisposable)System.Reactive.Disposables.Disposable.Empty;
            },
            _ => source);
    }

    /// <summary>
    /// Writes the mesh node at <paramref name="path"/> under the durable circuit user. Use from deferred
    /// callbacks (after an Rx hop / InvokeAsync) where the ambient AccessContext has been nulled — a bare
    /// <c>GetMeshNodeStream(path).Update(...)</c> would capture <see langword="null"/> and the owner would
    /// deny the write. <c>Update</c> captures <c>Context ?? CircuitContext</c> SYNCHRONOUSLY at the
    /// <c>.Update(...)</c> call, so a synchronous <c>SwitchAccessContext</c> around it stamps the durable
    /// user for that eager capture (an <c>Observable.Using</c> would run at Subscribe — too late).
    /// </summary>
    protected void UpdateMeshNodeAsCircuitUser(string path, Func<MeshNode, MeshNode> update, Action<Exception>? onError = null)
    {
        if (string.IsNullOrEmpty(path)) return;
        var hubAccess = Hub.ServiceProvider.GetService<AccessService>();
        var user = ResolveCircuitUser();
        using var _ = user is not null && hubAccess is not null
            ? hubAccess.SwitchAccessContext(user)
            : (IDisposable)System.Reactive.Disposables.Disposable.Empty;
        Hub.GetMeshNodeStream(path).Update(update).Subscribe(
            _ => { },
            ex =>
            {
                if (onError is not null) onError(ex);
                else Logger.LogWarning(ex, "Mesh node update under circuit user failed for {Path}", path);
            });
    }


    /// <summary>
    /// Returns <c>true</c> when the active theme is dark: resolves directly for explicit
    /// light/dark modes, or queries the browser via JS interop for the system mode.
    /// </summary>
    protected async Task<bool> IsDarkModeAsync()
    {
        return Mode switch
        {
            DesignThemeModes.Dark => true,
            DesignThemeModes.Light => false,
            _ => await GetSystemDarkModeAsync()
        };
    }

    private async Task<bool> GetSystemDarkModeAsync()
    {
        try
        {
            // Use getEffectiveTheme which considers both user preference and system settings
            var theme = await JSRuntime.InvokeAsync<string>("themeHandler.getEffectiveTheme");
            return theme == "dark";
        }
        catch (JSException)
        {
            // JS not available yet (prerendering) or themeHandler not loaded - default to light mode
            return false;
        }
    }
}

