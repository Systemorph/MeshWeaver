using System.Reactive.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using OpenSmc.Data;
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

    private IDisposable subscription;

    protected override void OnParametersSet()
    {
        base.OnParametersSet();
        if(Stream != null && Equals(Stream?.Owner, Address) && Equals(Stream?.Reference, Reference))
            return;

        if (Address == null)
            throw new ArgumentNullException(nameof(Address), "Address cannot be null.");
        if (Reference == null)
            throw new ArgumentNullException(nameof(Reference), "Reference cannot be null.");

        RootControl = null;
        Stream?.Dispose();

        Stream = Address.Equals(Hub.Address)
            ? Workspace.Stream.Reduce<JsonElement, LayoutAreaReference>(Reference, Address)
            : Workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(Address, Reference);

        subscription = Stream.GetControlStream(Reference.Area)
            .DistinctUntilChanged()
            .Subscribe(item => InvokeAsync(() => Render(item as UiControl)));
    }


    private void Render(UiControl control)
    {
        Logger.LogDebug(
            "Changing area {Reference} of {Address} to {Instance}",
            Reference,
            Address,
            control
        );
        RootControl = control;
        StateHasChanged();
    }


    public void Dispose()
    {
        subscription?.Dispose();
        Stream?.Dispose();
        Stream = null;
        subscription = null;
    }
}
