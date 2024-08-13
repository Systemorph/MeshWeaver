using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.FluentUI.AspNetCore.Components.DesignTokens;

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
        BindStream();
        BindViewModel();
        base.OnParametersSet();
    }

    private void BindViewModel()
    {
        DataBind(ViewModel.DisplayArea, x => x.DisplayArea);
        DataBind(ViewModel.ShowProgress, x => x.ShowProgress);
        DataBind(ViewModel.Reference.Layout ?? ViewModel.Reference.Area, x => x.AreaToBeRendered);
    }

    public string AreaToBeRendered { get; set; }

    private bool ShowProgress { get; set; }

    public override void Dispose()
    {
        Stream?.Dispose();
        Stream = null;
        base.Dispose();
    }

    private bool BindStream()
    {
        var address = ViewModel.Address;

        if (Stream is not null && Equals(Stream?.Owner, address) && Equals(Stream?.Reference, ViewModel.Reference))
            return false;

        if (address is null)
            throw new ArgumentNullException(nameof(address), "Address cannot be null.");
        if (ViewModel.Reference is null)
            throw new ArgumentNullException(nameof(Reference), "Reference cannot be null.");

        Area = ViewModel.Reference.Area;
        if (Area is null)
            throw new ArgumentNullException(nameof(Area), "Reference cannot be null.");
        Stream?.Dispose();

        Stream = address.Equals(Hub.Address)
            ? Workspace.Stream.Reduce<JsonElement, LayoutAreaReference>(ViewModel.Reference, address)
            : Workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, ViewModel.Reference);

        return true;
    }

}
