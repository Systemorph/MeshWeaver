using System.Linq.Expressions;
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

public class BlazorView<TViewModel, TView> : ComponentBase, IAsyncDisposable
    where TViewModel : IUiControl
    where TView : BlazorView<TViewModel, TView>
{
    [Inject] protected ILogger<TView> Logger { get; set; } = null!;
    [Inject] protected PortalApplication PortalApplication { get; set; } = null!;
    [Inject] protected IJSRuntime JSRuntime { get; set; } = null!;
    // The circuit's AccessService — CircuitAccessHandler sets the clicking user's identity on it. Used to
    // stamp user-driven messages (ClickedEvent) so they don't lose identity crossing the sync-stream
    // boundary (Stream.Hub is the sync hub, whose AccessService has no user context).
    [Inject] protected AccessService AccessService { get; set; } = null!;
    // The circuit's service provider — used to resolve the optional PortalErrorSink (modal surface) for
    // SurfaceError. Resolved lazily (GetService, not [Inject]) because non-portal hosts (e.g. the MAUI
    // client) don't register the sink.
    [Inject] protected IServiceProvider Services { get; set; } = null!;
    protected IMessageHub Hub => PortalApplication.Hub;
    [Parameter] public required TViewModel ViewModel { get; set; } 

    [Parameter] public ISynchronizationStream<JsonElement>? Stream { get; set; } 
    [Parameter] public string Area { get; set; } = null!;

    [CascadingParameter(Name = "Context")] public object? Context { get; set; }

    [CascadingParameter(Name = "ThemeMode")]
    public DesignThemeModes Mode { get; set; }

    [CascadingParameter(Name = nameof(DataContext))]
    public string? DataContext { get; set; }

    [CascadingParameter(Name = nameof(Model))]
    public ModelParameter<JsonElement>? Model { get; set; }

    protected string? Style { get; set; }

    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        BindDataAfterParameterReset();
    }

    protected virtual void BindDataAfterParameterReset()
    {
        Logger.LogDebug("Preparing data bindings for area {Area}", Area);
        DisposeBindings();
        BindData();
        Logger.LogDebug("Finished data bindings for area {Area}", Area);
    }


    protected string? Class { get; set; }
    protected string? Id { get; set; }

    protected List<IDisposable> Disposables { get; } = new();

    private bool _viewDisposed;

    /// <summary>True after <see cref="DisposeAsync"/> has been entered. Subscription callbacks
    /// can check this to avoid invoking <c>StateHasChanged</c> on a dead renderer.</summary>
    protected bool IsViewDisposed => _viewDisposed;

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

    protected void AddBinding(IDisposable binding)
    {
        bindings.Add(binding);
    }

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
                            ex => Logger.LogError(ex, "Node-bound binding faulted for '{pointer}' in Area {area}", reference.Pointer, Area)));
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
                                    Logger.LogWarning(ex,
                                        "Data binding for '{pointer}' in Area {area} faulted — rendering default so the control does not hang",
                                        reference.Pointer, Area);
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

    protected string SubArea(string area)
        => $"{Area}/{area}";

    protected virtual void UpdatePointer(object? value, JsonPointerReference reference)
    {
        // Node-bound DataContext: write the edited field straight back to the node stream
        // (per-field read-modify-write through IMeshNodeStreamCache). No /data replica, no
        // server-side save subscription — see LayoutAreaReference.MeshNodePrefix.
        if (LayoutAreaReference.TryParseMeshNodeDataContext(DataContext) is { } meshNode
            && !reference.Pointer.StartsWith('/'))
        {
            MeshNodeBindingExtensions.Write(Hub, Logger, meshNode.NodePath, meshNode.BindContent, meshNode.SubPath, reference, value);
            return;
        }

        if(Stream is null)
            throw new InvalidOperationException("Stream must be set before updating pointers.");
        Stream.UpdatePointer(value, DataContext ?? "/", reference, Model);
    }


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

    protected virtual void DisposeBindings()
    {
        foreach (var d in bindings)
            d.Dispose();
        bindings.Clear();
    }

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

