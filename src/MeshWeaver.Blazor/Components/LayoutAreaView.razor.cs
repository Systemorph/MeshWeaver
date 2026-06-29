using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Blazor.Services;
using MeshWeaver.Messaging;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Top-level Blazor component that hosts a single layout area from the mesh.
/// Manages the area's synchronization stream lifecycle (bind, rebind on parameter
/// change, dispose), surfaces progress/dialog sidecars, and wires the portal menu
/// when rendered as the top-level area.
/// </summary>
public partial class LayoutAreaView
{
    /// <summary>
    /// JavaScript runtime used for interop calls after the component has been
    /// rendered interactively (never called during pre-render).
    /// </summary>
    [Inject] protected IJSRuntime JsRuntime { get; set; } = null!;
    [Inject] private IMenuItemsProvider MenuItemsProvider { get; set; } = null!;

    private IWorkspace Workspace => Hub.GetWorkspace();


    private NamedAreaControl NamedArea =>
        new(Area) { ShowProgress = showProgress, ProgressMessage = progressMessage, SpinnerType = ViewModel.SpinnerType };

    /// <summary>
    /// Responds to parameter changes by re-binding the view-model and, when the
    /// area reference or target address has changed, disposing the stale stream
    /// and opening a fresh one. Skips stream binding during Blazor pre-render
    /// (only the first <c>OnAfterRenderAsync</c> triggers the actual stream).
    /// </summary>
    /// <param name="parameters">The incoming Blazor parameter set.</param>
    public override async Task SetParametersAsync(ParameterView parameters)
    {
        await base.SetParametersAsync(parameters);
        // ViewModel is declared `required` but Blazor's parameter pipeline still
        // feeds null through transient binding races (navigation, stream tear-down).
        // Mirror the guard in BlazorView.BindData — re-renders will arrive with a
        // populated ViewModel once the upstream parameter resolves.
        if (ViewModel is null) return;
        BindViewModel();
        var hadStream = AreaStream is not null;
        if (AreaStream is not null
            && (!AreaStream.Reference.Equals(ViewModel.Reference) ||
                !AreaStream.Owner.Equals(Address)))
        {
            Logger.LogDebug("[LAV] DISPOSE_STALE area={Area} addr={Address} ref={Ref} (parameters changed)",
                Area, ViewModel.Address, ViewModel.Reference);
            AreaStream.Dispose();
            AreaStream = null;
        }

        Logger.LogDebug("[LAV] SET_PARAMS area={Area} addr={Address} ref={Ref} preRender={PreRender} hadStream={HadStream} keepStream={KeepStream}",
            Area, Address, ViewModel.Reference, !IsNotPreRender, hadStream, AreaStream is not null);

        // Only bind stream when already in interactive mode (not during prerender)
        // try/catch: during navigation, old circuit's hub may already be disposing
        // while Blazor still re-renders components before their DisposeAsync runs
        if (IsNotPreRender)
            try { BindStream(); } catch (ObjectDisposedException) { }
    }
    private bool showProgress;
    private string? progressMessage;
    private DialogControl? currentDialog;
    private bool showDialog;
    private bool IsContentLoaded { get; set; }

    private void BindViewModel()
    {
        if (ViewModel is null) return;
        DataBind(ViewModel.ProgressMessage, x => x.progressMessage);
        DataBind(ViewModel.ShowProgress, x => x.showProgress);
        DataBind(ViewModel.Reference.Layout ?? ViewModel.Reference.Area, x => x.Area);
        DataBind(ViewModel.Address, x => x.Address, ConvertAddress!);
        DataBind(ViewModel.Reference.Layout ?? ViewModel.Reference.Area, x => x.Area);
        IsContentLoaded = true;
    }

    private Address? ConvertAddress(object address, Address _)
    {
        if (address is string s)
            return Hub.GetAddress(s);
        return address switch
        {
            JsonElement je => je.Deserialize<Address>(Hub.JsonSerializerOptions),
            JsonObject jo => jo.Deserialize<Address>(Hub.JsonSerializerOptions),
            _ => Hub.GetAddress(address.ToString()!)

        };
    }

    private Address? Address { get; set; }
    private ISynchronizationStream<JsonElement>? AreaStream { get; set; }
    /// <summary>
    /// Disposes the area, dialog, and progress streams when the component is torn down
    /// in interactive mode. In pre-render mode the streams were never bound, so disposal
    /// is a no-op beyond calling the base implementation.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        if (IsNotPreRender)
        {
            if (AreaStream != null)
            {
                Logger.LogDebug("LayoutAreaView disposing stream for {Area} (contentLoaded={ContentLoaded})",
                    Area, IsContentLoaded);
                AreaStream.Dispose();
            }
            DialogStream?.Dispose();
            ProgressStream?.Dispose();
            // Menu subscriptions were registered on AreaStream (above) and tore down with its Dispose().
        }
        else
        {
            Logger.LogDebug("LayoutAreaView disposed during prerender for {Area} — stream was never bound",
                Area);
        }
        AreaStream = null;
        DialogStream = null;
        ProgressStream = null;
        await base.DisposeAsync();
    }

    /// <summary>
    /// Finalizer safety-net: disposes any streams that were not released by
    /// <c>DisposeAsync</c> (e.g. if the component was abandoned without being
    /// properly torn down by the Blazor runtime).
    /// </summary>
    ~LayoutAreaView()
    {
        AreaStream?.Dispose();
        DialogStream?.Dispose();
        ProgressStream?.Dispose();
        AreaStream = null;
        DialogStream = null;
        ProgressStream = null;
    }

    // Must match NodeMenuItemsExtensions.NodeMenuContext / MeshMenuContext — duplicated here to avoid
    // a Blazor → Graph layer dependency.
    private const string NodeMenuContext = "Node";
    private const string MeshMenuContext = "Mesh";
    private const string AiMenuContext = "AI";

    private void BindStream()
    {
        if (AreaStream is null)
        {
            var isLocal = Address!.Equals(Workspace.Hub.Address);
            Logger.LogDebug("[LAV] BIND_STREAM area={Area} addr={Address} ref={Ref} mode={Mode}",
                Area, Address, ViewModel.Reference, isLocal ? "local" : "remote");
            AreaStream = isLocal
                ? Workspace.GetStream(ViewModel.Reference)!.Reduce(new JsonPointerReference("/"))
                : Workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(Address!, ViewModel.Reference);
            if (AreaStream is null)
                Logger.LogWarning("[LAV] BIND_STREAM_NULL area={Area} addr={Address} ref={Ref} — GetRemoteStream returned null",
                    Area, Address, ViewModel.Reference);
            DialogStream = SetupDialogAreaMonitoring(AreaStream!);
            DialogStream?.RegisterForDisposal(DialogStream.DistinctUntilChanged().Subscribe(
                el => OnDialogStreamChanged(el.Value),
                ex => OnReducedStreamError(ex, "dialog")));

            // Phase-aware loading label: bind the server-side progress item
            // ({ message, progress } in the EntityStore "data" collection — seeded
            // "Building layout…" by LayoutAreaHost.BuildInitialization, advanced by
            // the framework milestones / host.UpdateProgress, cleared on first
            // render). Riding the SAME area stream means the static "Subscribing…"
            // seed is replaced by live phases as soon as the first frame lands —
            // pure display, derived only from data already on the stream.
            ProgressStream = AreaStream!.Reduce(
                new JsonPointerReference(LayoutAreaReference.GetDataPointer(LayoutAreaHost.ProgressDataId)));
            ProgressStream?.RegisterForDisposal(ProgressStream.DistinctUntilChanged()
                .Subscribe(
                    el => OnProgressStreamChanged(el.Value),
                    ex => OnReducedStreamError(ex, "progress")));
            if (Top)
            {
                // Menus ride the SAME area stream, read via the renderer-agnostic hub.GetMenu API
                // (MeshWeaver.Mesh.MenuStreamExtensions) — the framework counterpart of hub.GetQuery.
                // No bespoke reduce + deserialize per context here; the native MAUI shell consumes the
                // exact same observable. Each subscription registers on AreaStream so AreaStream.Dispose()
                // tears it down (no separate menu-stream fields to track / null out).
                SubscribeMenu("", null);
                SubscribeMenu(NodeMenuContext, NodeMenuContext);
                SubscribeMenu(MeshMenuContext, MeshMenuContext);
                SubscribeMenu(AiMenuContext, AiMenuContext);
            }
        }
    }

    private ISynchronizationStream<JsonElement>? DialogStream { get; set; }
    private ISynchronizationStream<JsonElement>? ProgressStream { get; set; }

    /// <summary>
    /// onError for the dialog/menu/progress streams reduced off <see cref="AreaStream"/>. These are
    /// infra/display sidecars — the PRIMARY area-content error is surfaced visibly by
    /// <see cref="NamedAreaView"/>'s control-stream onError. Without an onError here, a fault on the
    /// reduced stream (e.g. a parameter-change re-bind that posts a context-less sync write →
    /// PostPipeline fails closed → the stream OnErrors) propagates UNHANDLED on the Rx scheduler
    /// thread and tears down the whole Blazor circuit — the "MW weg / page blanks on year/PK switch"
    /// symptom. Log it (Debug for benign teardown, Warning otherwise) and leave the last-good display;
    /// never let it blank the circuit. See Doc/GUI/DataBinding.md + Doc/Architecture/AccessContextPropagation.md.
    /// </summary>
    private void OnReducedStreamError(Exception error, string streamName)
    {
        if (IsViewDisposed || error is ObjectDisposedException)
        {
            Logger.LogDebug(error, "Suppressed teardown error in {Stream} stream for area {Area}", streamName, Area);
            return;
        }
        Logger.LogWarning(error, "Error in {Stream} stream for area {Area} — keeping last-good display (not tearing down the circuit)",
            streamName, Area);
    }

    /// <summary>
    /// Applies a server-side progress-item change ({ message, progress }) to the
    /// loading label. Non-empty message → show it (the area is still assembling:
    /// "Building layout…", "Initializing data sources…", "Rendering…", or a
    /// view-pushed phase via host.UpdateProgress). Empty message = content
    /// rendered — NamedAreaView hides the spinner via RootControl, so nothing to
    /// do. Pure display: derived exclusively from data already on the stream.
    /// </summary>
    private void OnProgressStreamChanged(JsonElement progressData)
    {
        try
        {
            if (!IsNotPreRender)
                return;
            if (progressData.ValueKind != JsonValueKind.Object)
                return;
            var message = progressData.TryGetProperty("message", out var m)
                          && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
            if (string.IsNullOrEmpty(message) || message == progressMessage)
                return;
            progressMessage = message;
            showProgress = true;
            InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error processing loading-progress stream change for area {Area}", Area);
        }
    }

    private ISynchronizationStream<JsonElement>? SetupDialogAreaMonitoring(ISynchronizationStream<JsonElement> areaStream)
    {
        return areaStream.Reduce(
            new JsonPointerReference(LayoutAreaReference.GetControlPointer(DialogControl.DialogArea)));
    }

    /// <summary>
    /// Subscribes the portal's <see cref="IMenuItemsProvider"/> slot <paramref name="providerContext"/>
    /// to the live items of <paramref name="menuContext"/> (<c>null</c> = the root <c>$Menu</c>) on the
    /// shared <see cref="AreaStream"/>, via the common <c>AreaStream.GetMenu(...)</c> API. Registered on
    /// AreaStream so it disposes with it. The reactive provider re-emits on permission changes, so the
    /// menu self-corrects without a reload.
    /// </summary>
    private void SubscribeMenu(string providerContext, string? menuContext)
    {
        if (!IsNotPreRender)
            return;
        AreaStream!.RegisterForDisposal(AreaStream!.GetMenu(menuContext).Subscribe(
            items => MenuItemsProvider.Update(providerContext, items),
            ex => OnReducedStreamError(ex, $"{providerContext} menu")));
    }

    private void OnDialogStreamChanged(JsonElement dialogData)
    {
        try
        {
            if (IsNotPreRender)
            {
                // Deserialize the dialog control from the stream
                if (dialogData.ValueKind != JsonValueKind.Null && dialogData.ValueKind != JsonValueKind.Undefined)
                {
                    var dialogControl = dialogData.Deserialize<DialogControl>(Hub.JsonSerializerOptions);
                    if (dialogControl != null)
                    {
                        if(dialogControl.Equals(currentDialog))
                            return; // No change, do nothing
                        currentDialog = dialogControl;
                        showDialog = true;
                        InvokeAsync(StateHasChanged);
                        return;
                    }
                }

                // If we get here, dialog was cleared or is null
                showDialog = false;
                currentDialog = null;
                InvokeAsync(StateHasChanged);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error processing dialog stream change");
        }
    }

    /// <summary>
    /// On the first render, marks the component as interactive (<c>IsNotPreRender = true</c>)
    /// and binds the area stream if it was not already bound during parameter setup.
    /// Subsequent renders are no-ops beyond the base implementation.
    /// </summary>
    /// <param name="firstRender"><c>true</c> on the very first interactive render; <c>false</c> on re-renders.</param>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        if (firstRender)
        {
            IsNotPreRender = true;
            Logger.LogDebug("[LAV] FIRST_RENDER area={Area} addr={Address} ref={Ref} hasStream={HasStream}",
                Area, Address, ViewModel?.Reference, AreaStream != null);
            // If we're now rendered and we don't have a stream yet, bind it
            if (AreaStream == null)
            {
                BindStream();
                StateHasChanged();
            }

        }
    }

    /// <summary>
    /// <c>true</c> once the component has completed its first interactive render.
    /// Stream binding and JavaScript interop are deferred until this flag is set
    /// to avoid executing browser-dependent code during server-side pre-render.
    /// </summary>
    protected bool IsNotPreRender { get; private set; }

}
