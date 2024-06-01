using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;
using OpenSmc.Messaging;

namespace OpenSmc.Blazor;

public partial class LayoutAreaView
{
    [Inject]
    private IMessageHub Hub { get; set; }
    [Inject] 
    private ILogger<LayoutAreaView> Logger { get; set; }

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

    private void Render(ChangeItem<EntityStore> item)
    {
        var newControl = item.Value.GetControl(Reference.Area);
        if(newControl==null)
            if(RootControl == null)
                return;
            else
            {
                RootControl = null;
                StateHasChanged();
                return;
            }

        if(newControl.Equals(RootControl))
            return;
        Logger.LogDebug("Changing area {Reference} of {Address} to {Instance}", Reference, Address, newControl);
        RootControl = newControl;
        StateHasChanged();
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Stream = Address.Equals(Hub.Address)
            ? Workspace.GetStream<EntityStore, LayoutAreaReference>(Reference)
            : Workspace.GetStream<EntityStore, LayoutAreaReference>(Address, Reference);
        Disposables.Add(Stream);
        Stream.Subscribe(item => InvokeAsync(() => Render(item)));
    }
}
