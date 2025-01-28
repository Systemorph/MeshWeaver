using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.Components;

public partial class LayoutAreaView
{
    [Inject] protected IJSRuntime JsRuntime { get; set; }

    private IWorkspace Workspace => Hub.GetWorkspace();

    private LayoutAreaProperties Properties { get; set; }

    private NamedAreaControl NamedArea =>
        new(Area) { ShowProgress = ShowProgress, ProgressMessage=ProgressMessage };

    public override async Task SetParametersAsync(ParameterView parameters)
    {
        await base.SetParametersAsync(parameters);
        BindViewModel();
        if (IsNotPreRender)
            BindStream();
        else
        {
            if (AreaStream != null)
                AreaStream.Dispose();
            AreaStream = null;
        }
    }

    protected override void OnAfterRender(bool firstRender)
    {
        base.OnAfterRender(firstRender);
        if(firstRender)
        {
            IsNotPreRender = true;
            StateHasChanged();
        }
    }


    private void BindViewModel()
    {
        DataBind(ViewModel.ProgressMessage, x => x.ProgressMessage);
        DataBind(ViewModel.ShowProgress, x => x.ShowProgress);
        DataBind(ViewModel.Reference.Layout ?? ViewModel.Reference.Area, x => x.Area);
        DataBind(ViewModel.Address, x => x.Address, ConvertAddress);
        DataBind(ViewModel.Reference.Layout ?? ViewModel.Reference.Area, x => x.Area);

    }

    private Address ConvertAddress(object address)
    {
        if (address is string s)
            return Hub.GetAddress(s);
        return Hub.GetAddress(address.ToString());
    }

    private Address Address { get; set; }
    private ISynchronizationStream<JsonElement> AreaStream { get; set; }
    public override async ValueTask DisposeAsync()
    {
        if (AreaStream != null)
            AreaStream.Dispose();
        AreaStream = null;
        await base.DisposeAsync();
    }
    private string RenderingArea { get; set; }
    private void BindStream()
    {
        if (AreaStream != null)
        {
            Logger.LogDebug("Disposing old stream for {Owner} and {Reference}", AreaStream.Owner, AreaStream.Reference);
            AreaStream.Dispose();
        }
        Logger.LogDebug("Acquiring stream for {Owner} and {Reference}", Address, ViewModel.Reference);
        AreaStream = Workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(Address, ViewModel.Reference);
    }

    protected bool IsNotPreRender { get; set; }

}
