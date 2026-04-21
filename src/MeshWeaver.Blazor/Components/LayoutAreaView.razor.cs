using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Blazor.Services;
using MeshWeaver.Messaging;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.Components;

public partial class LayoutAreaView
{
    [Inject] protected IJSRuntime JsRuntime { get; set; } = null!;
    [Inject] private IMenuItemsProvider MenuItemsProvider { get; set; } = null!;

    private IWorkspace Workspace => Hub.GetWorkspace();


    private NamedAreaControl NamedArea =>
        new(Area) { ShowProgress = showProgress, ProgressMessage = progressMessage, SpinnerType = ViewModel.SpinnerType };

    public override async Task SetParametersAsync(ParameterView parameters)
    {
        await base.SetParametersAsync(parameters);
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
            Area, Address, ViewModel?.Reference, !IsNotPreRender, hadStream, AreaStream is not null);

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
            MenuStream?.Dispose();
            NodeMenuStream?.Dispose();
            MeshMenuStream?.Dispose();
        }
        else
        {
            Logger.LogDebug("LayoutAreaView disposed during prerender for {Area} — stream was never bound",
                Area);
        }
        AreaStream = null;
        DialogStream = null;
        MenuStream = null;
        NodeMenuStream = null;
        MeshMenuStream = null;
        await base.DisposeAsync();
    }

    ~LayoutAreaView()
    {
        AreaStream?.Dispose();
        DialogStream?.Dispose();
        MenuStream?.Dispose();
        NodeMenuStream?.Dispose();
        MeshMenuStream?.Dispose();
        AreaStream = null;
        DialogStream = null;
        MenuStream = null;
        NodeMenuStream = null;
        MeshMenuStream = null;
    }

    // Must match NodeMenuItemsExtensions.NodeMenuContext / MeshMenuContext — duplicated here to avoid
    // a Blazor → Graph layer dependency.
    private const string NodeMenuContext = "Node";
    private const string MeshMenuContext = "Mesh";

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
            DialogStream?.RegisterForDisposal(DialogStream.DistinctUntilChanged().Subscribe(el => OnDialogStreamChanged(el.Value)));
            if (Top)
            {
                MenuStream = SetupMenuAreaMonitoring(AreaStream!, MenuControl.MenuArea);
                MenuStream?.RegisterForDisposal(MenuStream.DistinctUntilChanged().Subscribe(el => OnMenuStreamChanged(el.Value, "")));

                NodeMenuStream = SetupMenuAreaMonitoring(AreaStream!, MenuControl.GetMenuArea(NodeMenuContext));
                NodeMenuStream?.RegisterForDisposal(NodeMenuStream.DistinctUntilChanged().Subscribe(el => OnMenuStreamChanged(el.Value, NodeMenuContext)));

                MeshMenuStream = SetupMenuAreaMonitoring(AreaStream!, MenuControl.GetMenuArea(MeshMenuContext));
                MeshMenuStream?.RegisterForDisposal(MeshMenuStream.DistinctUntilChanged().Subscribe(el => OnMenuStreamChanged(el.Value, MeshMenuContext)));
            }
        }
    }

    private ISynchronizationStream<JsonElement>? DialogStream { get; set; }
    private ISynchronizationStream<JsonElement>? MenuStream { get; set; }
    private ISynchronizationStream<JsonElement>? NodeMenuStream { get; set; }
    private ISynchronizationStream<JsonElement>? MeshMenuStream { get; set; }

    private ISynchronizationStream<JsonElement>? SetupDialogAreaMonitoring(ISynchronizationStream<JsonElement> areaStream)
    {
        return areaStream.Reduce(
            new JsonPointerReference(LayoutAreaReference.GetControlPointer(DialogControl.DialogArea)));
    }

    private ISynchronizationStream<JsonElement>? SetupMenuAreaMonitoring(ISynchronizationStream<JsonElement> areaStream, string area)
    {
        return areaStream.Reduce(
            new JsonPointerReference(LayoutAreaReference.GetControlPointer(area)));
    }

    private void OnMenuStreamChanged(JsonElement menuData, string context)
    {
        try
        {
            if (IsNotPreRender)
            {
                if (menuData.ValueKind != JsonValueKind.Null && menuData.ValueKind != JsonValueKind.Undefined)
                {
                    var menuControl = menuData.Deserialize<MenuControl>(Hub.JsonSerializerOptions);
                    if (menuControl?.Items != null)
                    {
                        MenuItemsProvider.Update(context, menuControl.Items);
                        return;
                    }
                }

                MenuItemsProvider.Update(context, []);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error processing menu stream change for context '{Context}'", context);
        }
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

    protected bool IsNotPreRender { get; private set; }

}
