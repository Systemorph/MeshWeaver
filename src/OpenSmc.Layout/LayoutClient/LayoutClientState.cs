using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Newtonsoft.Json.Linq;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.LayoutClient;

public record LayoutClientState(LayoutClientConfiguration Configuration)
{
    internal ImmutableDictionary<(object ParentAddress, string Area), object> ControlAddressByParentArea { get; init; } = ImmutableDictionary<(object ParentAddress, string Area), object>.Empty;
    internal ImmutableDictionary<object, (object Address, string Area)> ParentsByAddress { get; init; } = ImmutableDictionary<object, (object Address, string Area)>.Empty;

    internal ImmutableDictionary<string, AreaChangedEvent> AreasByControlId { get; init; } = ImmutableDictionary<string, AreaChangedEvent>.Empty;
    internal ImmutableDictionary<object, AreaChangedEvent> AreasByControlAddress { get; init; } = ImmutableDictionary<object, AreaChangedEvent>.Empty;
    internal ImmutableList<(Func<LayoutClientState, AreaChangedEvent> Selector, IMessageDelivery Request)> PendingRequests { get; init; } = ImmutableList<(Func<LayoutClientState, AreaChangedEvent> Selector, IMessageDelivery Request)>.Empty;
    public ImmutableDictionary<(string Id, string Collection), object> DataEntities { get; init; } = ImmutableDictionary<(string Id, string Collection), object>.Empty;
    public ImmutableDictionary<(string Id, string Collection), ImmutableList<string>> Subscriptions { get; init; } = ImmutableDictionary<(string Id, string Collection), ImmutableList<string>>.Empty;
    public ImmutableDictionary<object, JsonObject> Workspaces { get; init; }

    public AreaChangedEvent GetById(string controlId)
        => AreasByControlId.GetValueOrDefault(controlId);
    public AreaChangedEvent GetByAddress(object address)
        => AreasByControlAddress.GetValueOrDefault(address);

    public AreaChangedEvent GetByIdAndArea(string id, string areaName)
        => AreasByControlId.TryGetValue(id, out var area) 
           && area.View is IUiControl control
            ? GetByAddressAndArea(control.Address, areaName)
            : null;

    public AreaChangedEvent GetByAddressAndArea(object address, string areaName)
    {
        if (!ControlAddressByParentArea.TryGetValue((address, areaName), out var controlAddress)
            || !AreasByControlAddress.TryGetValue(controlAddress, out var ret))
            return null;

        if (ret?.View is RemoteViewControl remoteView)
            ret = remoteView.Data as AreaChangedEvent;

        if (ret?.View is SpinnerControl)
            return null;

        return ret;
    }

}