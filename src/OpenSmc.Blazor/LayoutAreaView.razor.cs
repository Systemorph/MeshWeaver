using Microsoft.AspNetCore.Components;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;
using OpenSmc.Messaging;

namespace OpenSmc.Blazor;

public partial class LayoutAreaView : IDisposable
{
    [Inject]
    IMessageHub Hub { get; set; }

    private IWorkspace Workspace => Hub.GetWorkspace();

    private readonly List<IDisposable> disposables = new();

    public EntityStore LayoutAreaStore { get; private set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object> AdditionalParameters { get; set; }

    public override async Task SetParametersAsync(ParameterView parameters)
    {
        await base.SetParametersAsync(parameters);
        if (Control != null)
        {
            Address ??= Control.Address;
            Reference ??= Control.Reference;
        }

        if (Address == null)
            throw new ArgumentNullException(nameof(Address), "Address cannot be null.");
        if (Reference == null)
            throw new ArgumentNullException(nameof(Reference), "Reference cannot be null.");

        var changeStream = Address.Equals(Hub.Address)
            ? Workspace.GetChangeStream<EntityStore, LayoutAreaReference>(Reference)
            : Workspace.GetRemoteChangeStream<EntityStore, LayoutAreaReference>(Address, Reference);
        disposables.Add(changeStream);
        changeStream.Subscribe(Render);
        await changeStream.Initialized;
    }

    private void Render(ChangeItem<EntityStore> item)
    {
        LayoutAreaStore = item.Value;
        RootControl = LayoutAreaStore.GetControl(Reference.Area);
    }

    public void Dispose()
    {
        foreach (var disposable in disposables)
            disposable.Dispose();
        disposables.Clear();
    }
}
