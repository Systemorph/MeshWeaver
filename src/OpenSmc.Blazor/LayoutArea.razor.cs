using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;
using OpenSmc.Messaging;

namespace OpenSmc.Blazor;

public partial class LayoutArea
{
    [Inject]
    private IMessageHub Hub { get; set; }

    [Inject]
    private ILogger<LayoutArea> Logger { get; set; }

    private IWorkspace Workspace => Hub.GetWorkspace();

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object> AdditionalParameters { get; set; }

    public override async Task SetParametersAsync(ParameterView parameters)
    {
        await base.SetParametersAsync(parameters);
        if (ViewModel != null)
        {
            Address ??= ViewModel.Address;
            Reference ??= ViewModel.Reference;
        }

        if (Address == null)
            throw new ArgumentNullException(nameof(Address), "Address cannot be null.");
        if (Reference == null)
            throw new ArgumentNullException(nameof(Reference), "Reference cannot be null.");
    }

    private void Render(ChangeItem<JsonElement> item)
    {
        var newControl = GetControl(item, Reference.Area);
        if (newControl == null)
            if (RootControl == null)
                return;
            else
            {
                RootControl = null;
                StateHasChanged();
                return;
            }

        if (newControl.Equals(RootControl))
            return;
        Logger.LogDebug(
            "Changing area {Reference} of {Address} to {Instance}",
            Reference,
            Address,
            newControl
        );
        RootControl = newControl;
        StateHasChanged();
    }

    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        RootControl = null;
        Stream?.Dispose();

        Stream = Address.Equals(Hub.Address)
            ? Workspace.Stream.Reduce<JsonElement, LayoutAreaReference>(Reference, Address)
            : Workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(Address, Reference);
        Disposables.Add(Stream);
        Stream.Subscribe(item => InvokeAsync(() => Render(item)));
    }
}
