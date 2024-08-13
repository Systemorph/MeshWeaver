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
        new(AreaToBeRendered) { ShowProgress = ShowProgress, DisplayArea = DisplayArea };


    protected override void OnParametersSet()
    {
        if(IsUpToDate())
            return;

        BindStream();
        base.OnParametersSet();
    }

    protected override void BindData()
    {
        BindViewModel();
        base.BindData();
    }


    private void BindViewModel()
    {
        DataBind(ViewModel.DisplayArea, x => x.DisplayArea);
        DataBind(ViewModel.ShowProgress, x => x.ShowProgress);
        DataBind(ViewModel.Reference.Layout ?? ViewModel.Reference.Area, x => x.AreaToBeRendered);
    }

    public string AreaToBeRendered { get; set; }

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
        var address = ViewModel.Address;

        AreaStream?.Dispose();

        AreaStream = address.Equals(Hub.Address)
            ? Workspace.Stream.Reduce<JsonElement, LayoutAreaReference>(ViewModel.Reference, address)
            : Workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, ViewModel.Reference);

    }

}
