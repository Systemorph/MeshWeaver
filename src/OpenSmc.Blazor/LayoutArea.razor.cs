using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;
using OpenSmc.Messaging;

namespace OpenSmc.Blazor;

public partial class LayoutArea : IDisposable
{
    [Inject]
    private IMessageHub Hub { get; set; }

    [Inject]
    private ILogger<LayoutArea> Logger { get; set; }

    private IWorkspace Workspace => Hub.GetWorkspace();

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object> Options { get; set; }


    [Parameter]
    public object Address { get; set; }

    [Parameter]
    public LayoutAreaReference Reference { get; set; }

    public string Area { get; set; }
    public string DisplayArea { get; set; }


    [Parameter]
    public ISynchronizationStream<JsonElement, LayoutAreaReference> Stream { get; set; }

    private LayoutAreaProperties Properties { get; set; } 

    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        Area = Reference.Layout ?? Reference.Area;
        DisplayArea = Reference.DisplayArea ?? Reference.Area;

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
