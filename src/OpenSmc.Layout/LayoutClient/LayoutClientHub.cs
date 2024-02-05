using System.Collections.Immutable;
using System.Diagnostics;
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


public record LayoutClientConfiguration(object RefreshMessage, object LayoutHostAddress, string MainArea = "");

public class LayoutClientPlugin(LayoutClientConfiguration configuration, IMessageHub hub)
    : MessageHubPlugin<LayoutClientState>(hub),
        IMessageHandler<AreaChangedEvent>,
        IMessageHandler<GetRequest<AreaChangedEvent>>
{
    public override async Task StartAsync()
    {
        await base.StartAsync();
        InitializeState(new(configuration));
        Hub.Post(configuration.RefreshMessage, o => o.WithTarget(State.Configuration.LayoutHostAddress));
    }


    IMessageDelivery IMessageHandler<AreaChangedEvent>.HandleMessage(IMessageDelivery<AreaChangedEvent> request)
    {
        return UpdateArea(request);
    }

    private IMessageDelivery UpdateArea(IMessageDelivery<AreaChangedEvent> request)
    {
        var sender = request.Sender;
        if (sender.Equals(Hub.Address))
            return request;


        var areaChanged = request.Message;
        if(State.ControlAddressByParentArea.TryGetValue((sender, areaChanged.Area), out var controlAddress) &&
            State.AreasByControlAddress.TryGetValue(controlAddress, out var existing) )
        {
            if (IsUpToDate(areaChanged, existing))
                return request.Ignored();

            CheckOutArea(areaChanged);
            RemoveAreaFromParent(areaChanged);

        }

        if (areaChanged.View is IUiControl control)
        {
            var area = areaChanged;
            UpdateState(s =>s with{ControlAddressByParentArea = s.ControlAddressByParentArea.SetItem((sender, area.Area), control.Address)}) ;

            // the parent address might differ from sender, as the sender could be the top level logical hub,
            // which will forward the messages to the appropriate control
            var parentAddress = sender;
            if (string.IsNullOrEmpty(areaChanged.Area))
            {
                parentAddress = State.ParentsByAddress.TryGetValue(control.Address, out var parent)
                    ? parent.Address
                    : null;

            }

            areaChanged = CheckInArea(parentAddress, areaChanged);

            if (parentAddress != null)
                UpdateParents(parentAddress, areaChanged);

        }







        foreach (var (o, r) in State.PendingRequests.ToArray())
        {
            var el = o(State);
            if (el != null)
            {
                Hub.Post(el, oo => oo.ResponseFor(r));
                UpdateState(s => s with { PendingRequests = s.PendingRequests.Remove((o, r)) });
            }
        }

        return request.Processed();
    }


    private AreaChangedEvent CheckInArea(object parentAddress, AreaChangedEvent areaChanged)
    {
        var control = areaChanged.View as IUiControl;
        if (control == null)
            return areaChanged;

        areaChanged = areaChanged with { View = CheckInDynamic((dynamic)control) };
        UpdateState(s =>
            s with
            {
                ControlAddressByParentArea = s.ControlAddressByParentArea.SetItem((parentAddress, areaChanged.Area), control.Address),
                ParentsByAddress = s.ParentsByAddress.SetItem(control.Address, (parentAddress, areaChanged.Area)),
                AreasByControlAddress = s.AreasByControlAddress.SetItem(control.Address, areaChanged),
                AreasByControlId = s.AreasByControlId.SetItem(control.Id, areaChanged)
            });
        Hub.Post(new RefreshRequest(), o => o.WithTarget(control.Address));
        return areaChanged;
    }

    private void UpdateParents(object parentAddress, AreaChangedEvent areaChanged)
    {
        if (State.AreasByControlAddress.TryGetValue(parentAddress, out var parentArea))
        {
            if (parentArea.View is IUiControlWithSubAreas controlWithSubAreas)
            {
                controlWithSubAreas = controlWithSubAreas.SetArea(areaChanged);
                parentArea = parentArea with { View = controlWithSubAreas };
                State.ParentsByAddress.TryGetValue(controlWithSubAreas.Address, out var parentOfParent);
                UpdateState(s =>
                    s with
                    {
                        AreasByControlAddress = s.AreasByControlAddress.SetItem(controlWithSubAreas.Address, parentArea),
                        AreasByControlId = s.AreasByControlId.SetItem(controlWithSubAreas.Id, parentArea)
                    });
                UpdateParents(parentOfParent, parentArea);
            }
            else
            {
                Debug.Fail(areaChanged.ToString());
            }
        }
    }

    private bool IsUpToDate(AreaChangedEvent areaChanged, AreaChangedEvent existing)
    {
        if (areaChanged.View == null)
            return existing.View == null;

        if (areaChanged.View is IUiControl ctrl) return ctrl.IsUpToDate(existing.View);
        return areaChanged.View.Equals(existing.View);
    }


    private void CheckOutArea(AreaChangedEvent area)
    {
        if (area == null)
            return;
        RemoveAreaFromParent(area);

        if (area.View is not IUiControl existingControl)
            return;


        
        if (existingControl is IUiControlWithSubAreas controlWithSubAreas)
        {
            foreach (var subArea in controlWithSubAreas.SubAreas)
                CheckOutArea(subArea);
        }


        UpdateState(s => s with
        {
            ParentsByAddress = s.ParentsByAddress.Remove(existingControl.Address),
            AreasByControlAddress = s.AreasByControlAddress.Remove(existingControl.Address),
            AreasByControlId = s.AreasByControlId.Remove(existingControl.Id),
        });

    }

    private void RemoveAreaFromParent( AreaChangedEvent area)
    {
        if (State.ParentsByAddress.TryGetValue(area, out var parent)
            && State.AreasByControlAddress.TryGetValue(parent.Address, out var parentArea))
        {
            if (parentArea.View is IUiControlWithSubAreas controlWithSubAreas)
            {
                controlWithSubAreas = controlWithSubAreas.SetArea(area);
                UpdateParents(parent.Address, parentArea with { View = controlWithSubAreas });
            }
        }

    }


    // ReSharper disable once UnusedParameter.Local
    private object CheckInDynamic(UiControl control) => control;

    private object CheckInDynamic(LayoutStackControl stack)
    {
        //Post(new RefreshRequest(), o => o.WithTarget(stack.Address));
        var areas = stack.Areas.Select(a => CheckInArea(stack.Address, a)).ToArray();
        return stack with { Areas = areas };
    }

    private object CheckInDynamic(RedirectControl redirect)
    {
        Hub.Post(redirect.Message, o => o.WithTarget(redirect.RedirectAddress));
        if (redirect.Data is AreaChangedEvent area)
            return redirect with { Data = CheckInArea(redirect.Address, area) };
        return redirect;
    }


    IMessageDelivery IMessageHandler<GetRequest<AreaChangedEvent>>.HandleMessage(IMessageDelivery<GetRequest<AreaChangedEvent>> request)
    {
        if (request.Message.Options is not Func<LayoutClientState, AreaChangedEvent> selector)
        {
            throw new NotSupportedException();
        }

        var filtered = selector(State);
        if (filtered != null)
        {
            Hub.Post(filtered, o => o.ResponseFor(request));
            return request.Processed();
        }

        UpdateState(s => s with { PendingRequests = s.PendingRequests.Add((selector, request)) });
        return request.Forwarded();
    }
}

