using System.Collections.Immutable;
using System.Diagnostics;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.LayoutClient;

public record LayoutClientState(LayoutClientConfiguration Configuration)
{
    internal ImmutableDictionary<string, ImmutableDictionary<string, AreaChangedEvent>> AreasByControlId { get; init; } = ImmutableDictionary<string, ImmutableDictionary<string, AreaChangedEvent>>.Empty;
    internal ImmutableDictionary<object, AreaChangedEvent> AreasByControlAddress { get; init; } = ImmutableDictionary<object, AreaChangedEvent>.Empty;
    internal ImmutableDictionary<(object Address, string Area), AreaChangedEvent> AreasByAddressAndName { get; init; } = ImmutableDictionary<(object Address, string Area), AreaChangedEvent>.Empty;
    internal ImmutableList<(Func<LayoutClientState, AreaChangedEvent> Selector, IMessageDelivery Request)> PendingRequests { get; init; } = ImmutableList<(Func<LayoutClientState, AreaChangedEvent> Selector, IMessageDelivery Request)>.Empty;

    public IEnumerable<AreaChangedEvent> GetAreasByControlId(string controlId)
        => AreasByControlId.TryGetValue(controlId, out var dict)
               ? dict.Values
               : Enumerable.Empty<AreaChangedEvent>();

    public AreaChangedEvent GetAreaByName(string controlId, string areaName)
    {
        var ret = AreasByControlId.TryGetValue(controlId, out var dict)
            ? dict.GetValueOrDefault(areaName)
            : null;


        if (ret == null || ret.View is RemoteViewControl { Data: AreaChangedEvent { View: SpinnerControl } })
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
    public LayoutClientState StartupState()
        => new(configuration);


    public override async Task StartAsync()
    {
        await base.StartAsync();
        InitializeState(StartupState());
    }

    public override void InitializeState(LayoutClientState state)
    { 
        base.InitializeState(state);
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

        var control = request.Message.View as UiControl;
        State.AreasByAddressAndName.TryGetValue((sender, request.Message.Area), out var existing);
        if (existing == null && control != null && control.Address != null && State.AreasByControlAddress.TryGetValue(control.Address, out var inner))
            existing = inner;

        var areaChanged = request.Message;

        if (existing != null)
        {

            if (IsUpToDate(areaChanged, existing))
                return request.Ignored();

            if (existing.View is UiControl existingControl)
                CheckOutControl(existingControl);

        }

        if (State.AreasByControlAddress.TryGetValue(sender, out var parentArea))
        {
            if (parentArea.View is IUiControlWithSubAreas controlWithSubAreas)
            {
                controlWithSubAreas = controlWithSubAreas.SetArea(areaChanged);
                UpdateState(s => UpdateControlsRelatedState(s, controlWithSubAreas, new AreaChangedEvent("", controlWithSubAreas), areaChanged));
            }
            else
            {
                Debug.Fail(areaChanged.ToString());
            }
        }

        UpdateState(s => UpdateControlsRelatedState(s, control, sender, areaChanged));

        CheckInArea(areaChanged);

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

    private bool IsUpToDate(AreaChangedEvent areaChanged, AreaChangedEvent existing)
    {
        if (areaChanged.View == null)
            return existing.View == null;

        if (areaChanged.View is IUiControl ctrl) return ctrl.IsUpToDate(existing.View);
        return areaChanged.View.Equals(existing.View);
    }


    private void CheckOutControl(UiControl existingControl)
    {
        if (existingControl.Address == null)
            return;

        UpdateState(s => s with
                         {
                             AreasByControlAddress = s.AreasByControlAddress.Remove(existingControl.Address),
                             AreasByControlId = s.AreasByControlId.Remove(existingControl.Id)
                         });

        if(existingControl is IUiControlWithSubAreas controlWithSubAreas)
            foreach (var subArea in controlWithSubAreas.SubAreas)
                if (subArea.View is UiControl subAreaControl)
                    CheckOutControl(subAreaControl);
    }


    private void CheckInArea(AreaChangedEvent areaChanged)
    {
        if (areaChanged.View is UiControl control && control.Address != null)
        {
            UpdateState(s => UpdateControlsRelatedState(s, control, areaChanged));
            Hub.Post(new ConnectToHubRequest(Hub.Address, control.Address), o => o.WithTarget(control.Address));

            CheckInDynamic((dynamic)control);

        }

    }

    private static LayoutClientState UpdateControlsRelatedState(LayoutClientState s, IUiControl control,
        params AreaChangedEvent[] area)
    {
        return s with
        {
                   AreasByControlAddress = s.AreasByControlAddress.SetItems(area.Select(a => new KeyValuePair<object,AreaChangedEvent>(control.Address, a))),
                   AreasByControlId = s.AreasByControlId.SetItem(control.Id, (s.AreasByControlId.TryGetValue(control.Id, out var list) ? list : ImmutableDictionary<string, AreaChangedEvent>.Empty).SetItems(area.Select(a => new KeyValuePair<string, AreaChangedEvent>(a.Area,a))))
               };
    }

    private static LayoutClientState UpdateControlsRelatedState(LayoutClientState s, IUiControl control, object sender,
        params AreaChangedEvent[] area)
    {
        return UpdateControlsRelatedState(s, control, area) with
               {
                   AreasByAddressAndName = s.AreasByAddressAndName.SetItems(ConvertAreas(sender, area))
               };
    }

    private static IEnumerable<KeyValuePair<(object, string), AreaChangedEvent>> ConvertAreas(object sender, AreaChangedEvent[] area)
    {
        return area.Select(a => new KeyValuePair<(object,string),AreaChangedEvent>((sender, a.Area), a));
    }

    // ReSharper disable once UnusedParameter.Local
    private void CheckInDynamic(UiControl _) { }

    private void CheckInDynamic(RemoteViewControl remoteView)
    {
        Hub.Post(new RefreshRequest(nameof(RemoteViewControl.Data)), o => o.WithTarget(remoteView.Address));
    }
    private void CheckInDynamic(Composition.Layout stack)
    {
        //Post(new RefreshRequest(), o => o.WithTarget(stack.Address));
        foreach (var area in stack.Areas.ToArray())
        {
            CheckInArea(area);
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

