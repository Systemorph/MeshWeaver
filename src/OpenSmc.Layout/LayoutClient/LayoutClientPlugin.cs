using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenSmc.Data;
using OpenSmc.Layout.Composition;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.LayoutClient;

public class LayoutClientPlugin(LayoutClientConfiguration configuration, IMessageHub hub)
    : MessageHubPlugin<LayoutClientState>(hub),
        IMessageHandler<LayoutArea>,
        IMessageHandler<GetRequest<LayoutArea>>
{
    public override bool IsDeferred(IMessageDelivery delivery) 
        => delivery.Message is RefreshRequest
            || base.IsDeferred(delivery);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
        InitializeState(new(configuration));
        Hub.Post(configuration.RefreshRequest, o => o.WithTarget(State.Configuration.LayoutHostAddress));
    }


    IMessageDelivery IMessageHandler<LayoutArea>.HandleMessage(IMessageDelivery<LayoutArea> request)
    {
        return UpdateArea(request);
    }

    private IMessageDelivery UpdateArea(IMessageDelivery<LayoutArea> request)
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

        }

        if (areaChanged.Control is IUiControl control)
        {
            var area = areaChanged;
            UpdateState(s =>
                s with
                {
                    ControlAddressByParentArea = s.ControlAddressByParentArea.SetItem((sender, area.Area), control.Address)
                }) ;

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

    private LayoutArea CheckInArea(object parentAddress, LayoutArea areaChanged)
    {
        var control = areaChanged.Control as IUiControl;
        if (control == null)
            return areaChanged;

        areaChanged = areaChanged with { Control = CheckInDynamic((dynamic)control) };

        var subscriptions = ParseDataSubscriptions(control)
            .Select(x => new KeyValuePair<(string Collection, string Id), 
                ImmutableList<string>>(x, (State.Subscriptions.GetValueOrDefault(x) ?? ImmutableList<string>.Empty)
                .Add(areaChanged.Area)))
            .ToArray();

        UpdateState(s =>
            s with
            {
                ControlAddressByParentArea = s.ControlAddressByParentArea.SetItem((parentAddress, areaChanged.Area), control.Address),
                ParentsByAddress = s.ParentsByAddress.SetItem(control.Address, (parentAddress, areaChanged.Area)),
                AreasByControlAddress = s.AreasByControlAddress.SetItem(control.Address, areaChanged),
                AreasByControlId = s.AreasByControlId.SetItem(control.Id, areaChanged),
                Subscriptions = s.Subscriptions.SetItems(subscriptions)
            });

        Hub.Post(new RefreshRequest(), o => o.WithTarget(control.Address));
        return areaChanged;
    }

    private IEnumerable<(string Collection, string Id)> ParseDataSubscriptions(IUiControl control)
    {
        var dataEntities = FindDataEntities((control as UiControl)?.DataContext)
            .ToImmutableDictionary(x => (x.Collection, x.Id), x => x.Instance);
        
        if (!dataEntities.Any())
            return Enumerable.Empty<(string Collection, string Id)>();

        var dataHost = FindDataHost(control.Address);
        if(!State.Workspaces.TryGetValue(dataHost, out var workspace))
            UpdateState(s => s with{Workspaces = s.Workspaces.Add(dataHost, workspace = new())});


        var missingSubscriptions = new Dictionary<string,string>();
        foreach (var ( (collection, id), instance) in dataEntities)
        {
            var key = $"{collection}:{id}";
            if (workspace.TryAdd(key, instance))
                missingSubscriptions.Add(key, $"$['{collection}'][?(@['{ReservedProperties.Id}'] == '{id}')]");
        }

        if (missingSubscriptions.Any())
            Hub.Post(new SubscribeDataRequest(missingSubscriptions), o => o.WithTarget(dataHost));

        return dataEntities.Keys;
    }

    private object FindDataHost(object address)
    {
        return address is UiControlAddress uiControlAddress 
            ? FindDataHost(uiControlAddress.Host) 
            : address;
    }

    private record EntityDescriptor(string Id, string Collection, JsonObject Instance);

    private IEnumerable<EntityDescriptor> FindDataEntities(object dataContext)
    {
        if(dataContext == null)
            return Enumerable.Empty<EntityDescriptor>();

        var dataContextSerialized = (JsonObject)JsonNode.Parse(JsonSerializer.Serialize(dataContext));
            return FindDataEntities(dataContextSerialized);
    }

    private IEnumerable<EntityDescriptor> FindDataEntities(JsonObject jObject)
    {
        if (jObject.TryGetPropertyValue(ReservedProperties.Id, out var id) && jObject.TryGetPropertyValue(ReservedProperties.Type, out var type))
            yield return new(id!.ToString(), type!.ToString(), jObject);
        foreach (var child in jObject.Select(x => x.Value).OfType<JsonObject>())
                foreach (var entityDescriptor in FindDataEntities(child))
                    yield return entityDescriptor;
    }


    private bool IsUpToDate(LayoutArea areaChanged, LayoutArea existing)
    {
        if (areaChanged.Control == null)
            return existing.Control == null;

        if (areaChanged.Control is IUiControl ctrl) return ctrl.IsUpToDate(existing.Control);
        return areaChanged.Control.Equals(existing.Control);
    }


    private void CheckOutArea(LayoutArea area)
    {
        if (area == null)
            return;

        if (area.Control is not IUiControl existingControl)
            return;



        UpdateState(s => s with
        {
            ParentsByAddress = s.ParentsByAddress.Remove(existingControl.Address),
            AreasByControlAddress = s.AreasByControlAddress.Remove(existingControl.Address),
            AreasByControlId = s.AreasByControlId.Remove(existingControl.Id),
        });

    }


    // ReSharper disable once UnusedParameter.Local
    private object CheckInDynamic(UiControl control) => control;


    private object CheckInDynamic(RedirectControl redirect)
    {
        Hub.Post(redirect.Message, o => o.WithTarget(redirect.RedirectAddress));
        if (redirect.Data is LayoutArea area)
            return redirect with { Data = CheckInArea(redirect.Address, area) };
        return redirect;
    }


    IMessageDelivery IMessageHandler<GetRequest<LayoutArea>>.HandleMessage(IMessageDelivery<GetRequest<LayoutArea>> request)
    {
        if (request.Message.Options is not Func<LayoutClientState, LayoutArea> selector)
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

