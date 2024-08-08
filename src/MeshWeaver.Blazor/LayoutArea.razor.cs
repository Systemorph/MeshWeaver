using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor;

public partial class LayoutArea : IDisposable
{
    [Inject]
    private IMessageHub Hub { get; set; }

    [Inject]
    private ILogger<LayoutArea> Logger { get; set; }

    private IWorkspace Workspace => Hub.GetWorkspace();

    [Parameter]
    public string Id { get; set; }


    [Parameter]
    public object Address { get; set; }

    [Parameter]
    public LayoutAreaReference Reference { get; set; }

    public string Area { get; set; }
    public string DisplayArea { get; set; }


    public ISynchronizationStream<JsonElement, LayoutAreaReference> Stream { get; set; }

    private LayoutAreaProperties Properties { get; set; } 

    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        Area = Reference.Layout ?? Reference.Area;
        DisplayArea = Reference.Area;

        if(Stream != null && Equals(Stream?.Owner, Address) && Equals(Stream?.Reference, Reference))
            return;

        if (Address == null)
            throw new ArgumentNullException(nameof(Address), "Address cannot be null.");
        if (Reference == null)
            throw new ArgumentNullException(nameof(Reference), "Reference cannot be null.");

        Area ??= Reference.Area;

        Stream?.Dispose();

        Stream = Address.Equals(Hub.Address)
            ? Workspace.Stream.Reduce<JsonElement, LayoutAreaReference>(Reference, Address)
            : Workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(Address, Reference);


   }




    public void Dispose()
    {
        Stream?.Dispose();
        Stream = null;
    }
}
