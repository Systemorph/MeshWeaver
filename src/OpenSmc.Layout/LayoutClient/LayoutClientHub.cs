using System.Collections.Immutable;
using System.Diagnostics;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.LayoutClient;

public record LayoutClientState(LayoutClientConfiguration Configuration)
{
    internal ImmutableDictionary<string, ImmutableHashSet<object>> AreasByControlId { get; init; } = ImmutableDictionary<string, ImmutableHashSet<object>>.Empty;
    internal ImmutableDictionary<object, AreaChangedEvent> AreasByControlAddress { get; init; } = ImmutableDictionary<object, AreaChangedEvent>.Empty;
    internal ImmutableDictionary<(object Address, string Area), AreaChangedEvent> AreasByAddressAndName { get; init; } = ImmutableDictionary<(object Address, string Area), AreaChangedEvent>.Empty;
    internal ImmutableList<(Func<LayoutClientState, AreaChangedEvent> Selector, IMessageDelivery Request)> PendingRequests { get; init; } = ImmutableList<(Func<LayoutClientState, AreaChangedEvent> Selector, IMessageDelivery Request)>.Empty;

    public IEnumerable<AreaChangedEvent> GetAreasByControlId(string controlId)
        => AreasByControlId.TryGetValue(controlId, out var dict)
               ? dict.Select(a => AreasByControlAddress.TryGetValue(a, out var r) ? r : null).Where(x => x != null)
               : Enumerable.Empty<AreaChangedEvent>();

    public AreaChangedEvent GetAreasByName(string controlId, string areaName) => GetAreasByControlId(controlId).FirstOrDefault(area => area.Area == areaName);
}


public record LayoutClientConfiguration(object RefreshMessage, object LayoutHostAddress, string MainArea = "");

public class LayoutClientPlugin(LayoutClientConfiguration Configuration, IServiceProvider serviceProvider) : MessageHubPlugin<LayoutClientPlugin, LayoutClientState>(serviceProvider),
                               IMessageHandler<AreaChangedEvent>,
                               IMessageHandler<GetRequest<AreaChangedEvent>>
{
    public override void Initialize(LayoutClientState state)
    {
        base.Initialize(state);
        InitializeState(new(Configuration));
        Hub.Post(Configuration.RefreshMessage, o => o.WithTarget(State.Configuration.LayoutHostAddress));
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


        //if it is remote view = create remote view hub

        var areaChanged = request.Message;

        /*if (areaChanged.View is LayoutStackControl layoutStack)
        {
            var remoteView = Controls.RemoteView(new RefreshRequest(string.Empty), layoutStack.Address).withU;
        }*/
        

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
                UpdateState(s => s with
                                 {
                                     AreasByControlAddress = controlWithSubAreas == null || controlWithSubAreas.Address == null 
                                                                 ? s.AreasByControlAddress : 
                                                                 s.AreasByControlAddress.SetItem(controlWithSubAreas.Address, new AreaChangedEvent("", controlWithSubAreas)),
                                 });
            }
            else
            {
                Debug.Fail(areaChanged.ToString());
            }
        }

        UpdateState(s => UpdateControlsRelatedState(sender, areaChanged, s, control));

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
            UpdateState(s => UpdateControlsRelatedState(areaChanged, s, control));
            Hub.Post(new ConnectToHubRequest(Hub.Address, control.Address), o => o.WithTarget(control.Address));

            CheckInDynamic((dynamic)control);

        }

    }

    private LayoutClientState UpdateControlsRelatedState(AreaChangedEvent areaChanged, LayoutClientState s, UiControl control)
    {
        return s with
               {
                   AreasByControlAddress = s.AreasByControlAddress.SetItem(control.Address, areaChanged),
                   AreasByControlId = s.AreasByControlId.SetItem(control.Id, (s.AreasByControlId.TryGetValue(control.Id, out var dict) ?dict: ImmutableHashSet<object>.Empty).Add(control.Address)),
               };
    }

    private static LayoutClientState UpdateControlsRelatedState(object sender, AreaChangedEvent message, LayoutClientState s, IUiControl control)
    {
        return s with
               {
                   AreasByAddressAndName = s.AreasByAddressAndName.SetItem((sender, message.Area), message),
                   AreasByControlAddress = control == null || control.Address == null ? s.AreasByControlAddress : s.AreasByControlAddress.SetItem(control.Address, message),
                   AreasByControlId = control == null || control.Address == null ? s.AreasByControlId : s.AreasByControlId.SetItem(control.Id, (s.AreasByControlId.TryGetValue(control.Id, out var list) ? list : ImmutableHashSet<object>.Empty).Add(control))
               };
    }

    // ReSharper disable once UnusedParameter.Local
    private void CheckInDynamic(UiControl _) { }

    private void CheckInDynamic(RemoteViewControl remoteView)
    {
        Hub.Post(new RefreshRequest(nameof(RemoteViewControl.Data)), o => o.WithTarget(remoteView.Address));
    }
    private void CheckInDynamic(LayoutStackControl stack)
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

