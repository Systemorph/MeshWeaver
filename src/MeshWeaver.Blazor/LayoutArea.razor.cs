using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor;

[StreamRendering]
public partial class LayoutArea
{
    [Inject] private IMessageHub Hub { get; set; }

    [Inject] protected IJSRuntime JsRuntime { get; set; }

    private IWorkspace Workspace => Hub.GetWorkspace();

    private LayoutAreaProperties Properties { get; set; }
    public string DisplayArea { get; set; }

    private NamedAreaControl NamedArea =>
        new(Area) { ShowProgress = ShowProgress, DisplayArea = DisplayArea };

    public override async Task SetParametersAsync(ParameterView parameters)
    {
        await base.SetParametersAsync(parameters);
        BindViewModel();
        if (IsNotPreRender)
            BindStream();
        else
        {
            AreaStream?.Dispose();
            AreaStream = null;
        }
    }


    private void BindViewModel()
    {
        DataBind(ViewModel.DisplayArea, x => x.DisplayArea);
        DataBind(ViewModel.ShowProgress, x => x.ShowProgress);
        DataBind(ViewModel.Reference.Layout ?? ViewModel.Reference.Area, x => x.Area);
    }


    private bool ShowProgress { get; set; }

    private ISynchronizationStream<JsonElement, LayoutAreaReference> AreaStream { get; set; }
    public override void Dispose()
    {
        AreaStream?.Dispose();
        AreaStream = null;
        base.Dispose();
    }
    private string RenderingArea { get; set; }
    private void BindStream()
    {
        if (AreaStream != null)
        {
            Logger.LogDebug("Disposing old stream for {Owner} and {Reference}", AreaStream.Owner, AreaStream.Reference);
            AreaStream.Dispose();
        }
        Logger.LogDebug("Acquiring stream for {Owner} and {Reference}", ViewModel.Address, ViewModel.Reference);
        AreaStream = Workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(ViewModel.Address, ViewModel.Reference);
    }

    protected bool IsNotPreRender => (bool)JsRuntime.GetType().GetProperty("IsInitialized")!.GetValue(JsRuntime)!;

}
