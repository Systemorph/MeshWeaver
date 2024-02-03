using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using Castle.Core.Smtp;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.LayoutClient;

public record LayoutClientState(LayoutClientConfiguration Configuration)
{
    internal ImmutableDictionary<(object Address, string Area), object> ControlAddressBySenderAndArea { get; init; } = ImmutableDictionary<(object Address, string Area), object>.Empty;

    internal ImmutableDictionary<string, ImmutableDictionary<string, AreaChangedEvent>> AreasByControlId { get; init; } = ImmutableDictionary<string, ImmutableDictionary<string, AreaChangedEvent>>.Empty;
    internal ImmutableDictionary<object, ImmutableDictionary<string, AreaChangedEvent>> AreasByControlAddress { get; init; } = ImmutableDictionary<object, ImmutableDictionary<string, AreaChangedEvent>>.Empty;
    internal ImmutableList<(Func<LayoutClientState, AreaChangedEvent> Selector, IMessageDelivery Request)> PendingRequests { get; init; } = ImmutableList<(Func<LayoutClientState, AreaChangedEvent> Selector, IMessageDelivery Request)>.Empty;

    public IEnumerable<AreaChangedEvent> GetAreasByControlId(string controlId)
        => AreasByControlId.TryGetValue(controlId, out var dict)
               ? dict.Values
               : Enumerable.Empty<AreaChangedEvent>();

    public AreaChangedEvent GetAreaById(string controlId, string areaName)
    {
        var ret = AreasByControlId.TryGetValue(controlId, out var dict)
            ? dict.GetValueOrDefault(areaName)
            : null;


        if (ret?.View is RemoteViewControl remoteView)
            ret = remoteView.Data as AreaChangedEvent;

        if (ret?.View is SpinnerControl)
            return null;

        return ret;
    }
}


public record LayoutClientConfiguration(object RefreshMessage, object LayoutHostAddress, string MainArea = "");

public class LayoutClientPlugin(LayoutClientConfiguration configuration, IMessageHub hub)
    : MessageHubPlugin<LayoutClientPlugin, LayoutClientState>(hub),
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
            return request.Ignored();

        var areaChanged = request.Message;
        if(State.ControlAddressBySenderAndArea.TryGetValue((sender, areaChanged.Area), out var address)
            &&
            State.AreasByControlAddress.TryGetValue(address, out var areas) &&
            areas.TryGetValue(string.Empty, out var existing))
        {
            if (IsUpToDate(areaChanged, existing))
                return request.Ignored();

            if (existing.View is IUiControl existingControl)
                CheckOutControl(sender,areaChanged.Area, existingControl);

        }

        CheckInArea(sender, areaChanged);
        UpdateParents(sender, areaChanged);




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

    private void UpdateParents(object sender, AreaChangedEvent areaChanged)
    {
        if (!string.IsNullOrEmpty(areaChanged.Area)
            && State.AreasByControlAddress.TryGetValue(sender, out var inner) 
            && inner.TryGetValue(string.Empty, out var parentArea))
        {
            if (parentArea.View is IUiControlWithSubAreas controlWithSubAreas)
            {
                controlWithSubAreas = controlWithSubAreas.SetArea(areaChanged);
                parentArea = parentArea with { View = controlWithSubAreas };
                UpdateState(s =>
                    s with
                    {
                        AreasByControlAddress = AddTo(s.AreasByControlAddress, controlWithSubAreas.Address, parentArea),
                        AreasByControlId = AddTo(s.AreasByControlId, controlWithSubAreas.Id, parentArea)
                    });
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


    private void CheckOutControl(object sender, string area, IUiControl existingControl)
    {
        if (existingControl.Address == null)
            throw new ArgumentException($"No address set in control", nameof(existingControl));

        UpdateState(s => s with
                         {
                             ControlAddressBySenderAndArea = s.ControlAddressBySenderAndArea.Remove((sender, area)),
                             AreasByControlAddress = s.AreasByControlAddress.Remove(existingControl.Address),
                             AreasByControlId = s.AreasByControlId.Remove(existingControl.Id)
                         });



        if (existingControl is IUiControlWithSubAreas controlWithSubAreas)
            foreach (var subArea in controlWithSubAreas.SubAreas)
                if (subArea.View is UiControl subAreaControl)
                    CheckOutControl(existingControl.Address, subArea.Area, subAreaControl);
    }

    private static ImmutableDictionary<T, ImmutableDictionary<string, AreaChangedEvent>> RemoveFrom<T>(ImmutableDictionary<T, ImmutableDictionary<string, AreaChangedEvent>> ret, Func<IUiControl, T> key, IUiControl existingControl)
    {
        ret = ret.Remove(key(existingControl));
        return ret;
    }
    private static ImmutableDictionary<T, ImmutableDictionary<string, AreaChangedEvent>> AddTo<T>(ImmutableDictionary<T, ImmutableDictionary<string, AreaChangedEvent>> ret, T key, AreaChangedEvent area)
    {
        ret = ret.SetItem(key, 
            (ret.TryGetValue(key, out var list) ? list : ImmutableDictionary<string, AreaChangedEvent>.Empty)
            .SetItem(area.Area, area));
        return ret;
    }


    private void CheckInArea(object parentAddress, AreaChangedEvent areaChanged)
    {
        var control = areaChanged.View as IUiControl;
        if(control == null)
            return;
        UpdateState(s =>
            s with
            {
                ControlAddressBySenderAndArea =
                s.ControlAddressBySenderAndArea.SetItem((parentAddress, areaChanged.Area), control.Address),
                AreasByControlAddress = AddTo(s.AreasByControlAddress, control.Address, areaChanged),
                AreasByControlId = AddTo(s.AreasByControlId, control.Id, areaChanged)


            });
        Hub.Post(new RefreshRequest(), o => o.WithTarget(control.Address));
        CheckInDynamic((dynamic)control);
    }


    //private static ImmutableDictionary<string, AreaChangedEvent> SetToControlId(LayoutClientState s, IUiControl control, AreaChangedEvent area, string areaName)
    //{
    //    return (s.AreasByControlId.TryGetValue(control.Id, out var list) ? list : ImmutableDictionary<string, AreaChangedEvent>.Empty).SetItem(areaName, area);
    //}

    //private static ImmutableDictionary<string, AreaChangedEvent> SetToAreasByControlAddress(LayoutClientState s, IUiControl control, AreaChangedEvent area)
    //{
    //    return (s.AreasByControlAddress.TryGetValue(control.Address, out var hs) ? hs :ImmutableDictionary<string, AreaChangedEvent>.Empty ).SetItem(area.Area, area);
    //}

    //private static LayoutClientState UpdateControlsRelatedState(LayoutClientState s, IUiControl control, object sender, AreaChangedEvent area)
    //{
    //    return UpdateControlsRelatedState(s, control, area) with
    //           {
    //               AreasByAddressAndName = s.AreasByAddressAndName.SetItem(ConvertAreas(sender, area))
    //           };
    //}


    // ReSharper disable once UnusedParameter.Local
    private void CheckInDynamic(UiControl _) { }

    private void CheckInDynamic(LayoutStackControl stack)
    {
        //Post(new RefreshRequest(), o => o.WithTarget(stack.Address));
        foreach (var area in stack.Areas.ToArray())
        {
            CheckInArea(stack.Address, area);
        }
    }

    private void CheckInDynamic(RedirectControl redirect)
    {
        Hub.Post(redirect.Message, o => o.WithTarget(redirect.RedirectAddress));
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

