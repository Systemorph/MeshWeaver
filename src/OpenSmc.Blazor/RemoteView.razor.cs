using Microsoft.AspNetCore.Components;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Layout;
using OpenSmc.Messaging;

namespace OpenSmc.Blazor;

public partial class RemoteView : IDisposable
{
    [Inject]
    IMessageHub Hub { get; set; }

    [Inject]
    IWorkspace Workspace { get; set; }


    private readonly List<IDisposable> disposables = new List<IDisposable>();

    public EntityStore LayoutAreaStore { get; private set; }

    public override async Task SetParametersAsync(ParameterView parameters)
    {
        await base.SetParametersAsync(parameters);
        if (Control != null)
        {
            Address ??= Control.Address;
            Reference ??= Control.Reference;
        }
        var remoteStream = Workspace.GetRemoteChangeStream<EntityStore, LayoutAreaReference>(
            Address,
            Reference
        );
        disposables.Add(remoteStream);
        remoteStream.Subscribe(Render);
    }

    private void Render(ChangeItem<EntityStore> item)
    {
        if (Hub.Address.Equals(item.Address))
            return;
        LayoutAreaStore = item.Value;
        var rootItem = LayoutAreaStore.GetControl(Reference.Area);
        if (rootItem == null)
            return; //TODO Roland Bürgi 2024-05-24: Need to find out what should happen in this case ==> view should be disposed.
    }

    public void Dispose()
    {
        foreach (var disposable in disposables)
            disposable.Dispose();
        disposables.Clear();
    }
}
