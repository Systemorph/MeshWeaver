using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor;

public partial class LayoutArea
{
    [Inject]
    private IMessageHub Hub { get; set; }

    [Inject]
    private ILogger<LayoutArea> Logger { get; set; }

    private IWorkspace Workspace => Hub.GetWorkspace();




    private LayoutAreaProperties Properties { get; set; }
    public string DisplayArea { get; set; }

    private NamedAreaControl NamedArea =>
        new(Area) { ShowProgress = ShowProgress, DisplayArea = DisplayArea };

    public override async Task SetParametersAsync(ParameterView parameters)
    {
        await base.SetParametersAsync(parameters);
        BindStream();
        BindViewModel();
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
            if (ViewModel.Address.Equals(AreaStream.Owner) && ViewModel.Reference.Equals(AreaStream.Reference))
                return;
            AreaStream.Dispose();
        }

        AreaStream = ViewModel.Address.Equals(Hub.Address)
            ? Workspace.Stream.Reduce<JsonElement, LayoutAreaReference>(ViewModel.Reference, ViewModel.Address)
            : Workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(ViewModel.Address, ViewModel.Reference);

    }

}
