using System.Collections.Immutable;
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

    public AreaChangedEvent GetAreaByControlId(string controlId)
        => CollectionExtensions.GetValueOrDefault(AreasByControlId, controlId);

    public AreaChangedEvent GetAreaByIdAndArea(string id, string areaName)
        => AreasByControlId.TryGetValue(id, out var area) 
           && area.View is IUiControl control
            ? GetAreaByAddressAndArea(control.Address, areaName)
            : null;

    public AreaChangedEvent GetAreaByAddressAndArea(object address, string areaName)
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

    public AreaChangedEvent GetAreaById(string controlId)
        => AreasByControlId.GetValueOrDefault(controlId);
}