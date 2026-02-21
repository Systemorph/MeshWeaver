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
        if (AreaStream is not null
            && (!AreaStream.Reference.Equals(ViewModel.Reference) ||
                !AreaStream.Owner.Equals(ViewModel.Address)))
        {
            AreaStream.Dispose();
            AreaStream = null;
        }

        // Only bind stream when already in interactive mode (not during prerender)
        if (IsNotPreRender)
            BindStream();
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
                AreaStream.Dispose();
            if (DialogStream != null)
                DialogStream.Dispose();
            if (MenuStream != null)
                MenuStream.Dispose();
        }
        AreaStream = null;
        DialogStream = null;
        MenuStream = null;
        await base.DisposeAsync();
    }

    ~LayoutAreaView()
    {
        AreaStream?.Dispose();
        DialogStream?.Dispose();
        MenuStream?.Dispose();
        AreaStream = null;
        DialogStream = null;
        MenuStream = null;
    }
    private void BindStream()
    {
        if (AreaStream is null)
        {
            Logger.LogDebug("Acquiring stream for {Owner} and {Reference}", Address!, ViewModel.Reference);
            AreaStream = Address!.Equals(Workspace.Hub.Address)
                ? Workspace.GetStream(ViewModel.Reference)!.Reduce(new JsonPointerReference("/"))
                : Workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(Address!, ViewModel.Reference);
            DialogStream = SetupDialogAreaMonitoring(AreaStream!);
            DialogStream?.RegisterForDisposal(DialogStream.DistinctUntilChanged().Subscribe(el => OnDialogStreamChanged(el.Value)));
            MenuStream = SetupMenuAreaMonitoring(AreaStream!);
            MenuStream?.RegisterForDisposal(MenuStream.DistinctUntilChanged().Subscribe(el => OnMenuStreamChanged(el.Value)));
        }
    }

    private ISynchronizationStream<JsonElement>? DialogStream { get; set; }
    private ISynchronizationStream<JsonElement>? MenuStream { get; set; }

    private ISynchronizationStream<JsonElement>? SetupDialogAreaMonitoring(ISynchronizationStream<JsonElement> areaStream)
    {
        return areaStream.Reduce(
            new JsonPointerReference(LayoutAreaReference.GetControlPointer(DialogControl.DialogArea)));
    }

    private ISynchronizationStream<JsonElement>? SetupMenuAreaMonitoring(ISynchronizationStream<JsonElement> areaStream)
    {
        return areaStream.Reduce(
            new JsonPointerReference(LayoutAreaReference.GetControlPointer(MenuControl.MenuArea)));
    }

    private void OnMenuStreamChanged(JsonElement menuData)
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
                        MenuItemsProvider.Update(menuControl.Items);
                        return;
                    }
                }

                MenuItemsProvider.Update([]);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error processing menu stream change");
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
