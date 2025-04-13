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
        new(Area) { ShowProgress = showProgress, ProgressMessage=progressMessage };

    public override async Task SetParametersAsync(ParameterView parameters)
    {
        await base.SetParametersAsync(parameters);
        BindViewModel();
        if (AreaStream is not null
            && (!AreaStream.Reference.Equals(ViewModel?.Reference) ||
                !AreaStream.Owner.Equals(ViewModel?.Address)))
        {
            AreaStream.Dispose();
            AreaStream = null;
        }

        if (IsNotPreRender)
            BindStream();
    }
    private bool showProgress;
    private string progressMessage;

    private void BindViewModel()
    {
        DataBind(ViewModel.ProgressMessage, x => x.progressMessage);
        DataBind(ViewModel.ShowProgress, x => x.showProgress);
        DataBind(ViewModel.Reference.Layout ?? ViewModel.Reference.Area, x => x.Area);
        DataBind(ViewModel.Address, x => x.Address, ConvertAddress);
        DataBind(ViewModel.Reference.Layout ?? ViewModel.Reference.Area, x => x.Area);

    }

    private Address ConvertAddress(object address, Address _)
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
        if (AreaStream is null)
        {
            //Logger.LogDebug("Disposing old stream for {Owner} and {Reference}", AreaStream.Owner, AreaStream.Reference);
            //AreaStream.Dispose();
            Logger.LogDebug("Acquiring stream for {Owner} and {Reference}", Address, ViewModel.Reference);
            AreaStream = Address.Equals(Workspace.Hub.Address)
                ? Workspace.GetStream(ViewModel.Reference).Reduce(new JsonPointerReference("/"))
                : Workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(Address, ViewModel.Reference);
        }
    }


    protected bool IsNotPreRender => (bool)JsRuntime.GetType().GetProperty("IsInitialized")!.GetValue(JsRuntime)!;

}
