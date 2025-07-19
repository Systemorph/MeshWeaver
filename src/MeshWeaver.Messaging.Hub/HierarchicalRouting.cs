using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

internal class HierarchicalRouting
{
    private readonly IMessageHub? parentHub;

    private readonly ILogger<HierarchicalRouting> logger;
    private readonly RouteConfiguration configuration;
    private readonly IMessageHub hub;


    internal HierarchicalRouting(IMessageHub hub, IMessageHub? parentHub)
    {
        this.parentHub = parentHub;
        this.hub = hub;
        this.logger = hub.ServiceProvider.GetRequiredService<ILogger<HierarchicalRouting>>();
        this.configuration = hub
            .Configuration
            .GetListOfRouteLambdas()
            .Aggregate(new RouteConfiguration(hub), (c, f) => f.Invoke(c));
    }



    /// <summary>
    /// Loops through forward rules in a sequence. Each forward rule either applies and returns delivery.Forwarded() or doesn't apply and returns delivery.
    /// </summary>
    /// <param name="delivery"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IMessageDelivery> RouteMessageAsync(IMessageDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (delivery.State != MessageDeliveryState.Submitted)
            return delivery;


        // TODO V10: This should probably also react upon disconnect. (02.02.2024, Roland Bürgi)
        if (configuration.RoutedMessageAddresses.TryGetValue(delivery.Sender, out var originalSenders))
        {
            foreach (var originalSender in originalSenders)
            {
                logger.LogDebug(
                    "Routing message {id} of type {type} from address {sender} to address {target} to original address {originalSender}",
                    delivery.Id, delivery.Message.GetType().Name, delivery.Sender, delivery.Target, originalSender);
                var delivery1 = delivery;
                hub.Post(delivery.Message, o => o.WithTarget(originalSender).WithProperties(delivery1.Properties));
            }

        }

        foreach (var handler in configuration.Handlers)
            delivery = await handler(delivery, cancellationToken);

        if (delivery.State != MessageDeliveryState.Submitted)
            return delivery;

        if (delivery.Target is null || delivery.Target.Equals(hub.Address) ||
            (delivery.Target is HostedAddress ha && hub.Address.Equals(ha.Address)))
            return delivery;

        return RouteAlongHostingHierarchy(delivery);
    }

    private IMessageDelivery RouteAlongHostingHierarchy(IMessageDelivery delivery)
    {
        if (delivery.Target is null)
            return delivery;

        // Check if hub is disposing and reject hosted hub routing to prevent deadlocks
        if (hub.IsDisposing)
        {
            logger.LogWarning("Rejecting message routing for {MessageType} to {Target} - hub {Address} is disposing", 
                delivery.Message.GetType().Name, delivery.Target, hub.Address);
            
            hub.Post(
                new DeliveryFailure(delivery)
                {
                    ErrorType = ErrorType.Rejected,
                    Message = $"Cannot route message to {delivery.Target} - hub {hub.Address} is disposing"
                }, o => o.ResponseFor(delivery)
            );
            return delivery.Failed("Hub disposing");
        }

        if (delivery.Target is HostedAddress hosted)
        {
            logger.LogDebug("Routing delivery {id} of type {type} to host with address {target}", delivery.Id,
                delivery.Message.GetType().Name, hosted.Host);
            if (hub.Address.Equals(hosted.Host))
            {
                var nextLevelAddress = hosted.Address;
                if (nextLevelAddress is HostedAddress hostedInner)
                    nextLevelAddress = hostedInner.Host;
                
                // During disposal, only look for existing hubs, don't create new ones
                var creation = hub.IsDisposing ? HostedHubCreation.Never : HostedHubCreation.Always;
                var hostedHub = hub.GetHostedHub(nextLevelAddress, x => x, creation);
                
                if (hostedHub is not null)
                {
                    hostedHub.DeliverMessage(delivery.WithTarget(hosted.Address));
                    return delivery.Forwarded();
                }
                
                var errorMessage = hub.IsDisposing 
                    ? $"No existing route found for host {hosted.Address} and hub {hub.Address} is disposing"
                    : $"No route found for host {hosted.Address}. Last tried in {hub.Address}";
                    
                logger.LogDebug(errorMessage);
                hub.Post(
                    new DeliveryFailure(delivery)
                    {
                        ErrorType = hub.IsDisposing ? ErrorType.Rejected : ErrorType.NotFound,
                        Message = errorMessage
                    }, o => o.ResponseFor(delivery)
                );
                return hub.IsDisposing ? delivery.Failed("Hub disposing") : delivery.NotFound();
            }
        }
        else
        {
            var hostedHub = hub.GetHostedHub(delivery.Target ?? throw new ArgumentNullException(nameof(delivery.Target)), HostedHubCreation.Never);
            if (hostedHub is not null)
            {
                hostedHub.DeliverMessage(delivery);
                return delivery.Forwarded();
            }
        }

        if (parentHub == null)
        {
            var errorMessage = hub.IsDisposing 
                ? $"No route found for {delivery.Target} and hub {hub.Address} is disposing"
                : $"No route found for host {delivery.Target}. Last tried in {hub.Address}";
                
            logger.LogDebug(errorMessage);
            hub.Post(
                new DeliveryFailure(delivery)
                {
                    ErrorType = hub.IsDisposing ? ErrorType.Rejected : ErrorType.NotFound,
                    Message = errorMessage
                }, o => o.ResponseFor(delivery)
            );
            return hub.IsDisposing ? delivery.Failed("Hub disposing") : delivery.NotFound();
        }

        // Check if parent hub is also disposing before routing up
        if (parentHub.IsDisposing)
        {
            logger.LogWarning("Cannot route to parent hub {ParentAddress} - parent is also disposing. Message: {MessageType}", 
                parentHub.Address, delivery.Message.GetType().Name);
            
            hub.Post(
                new DeliveryFailure(delivery)
                {
                    ErrorType = ErrorType.Rejected,
                    Message = $"Cannot route to parent hub {parentHub.Address} - parent hub is also disposing"
                }, o => o.ResponseFor(delivery)
            );
            return delivery.Failed("Parent hub disposing");
        }
        
        logger.LogDebug("Routing delivery {id} of type {type} to parent {target}", delivery.Id,
            delivery.Message.GetType().Name, parentHub.Address);
        if (parentHub.Address is not MeshAddress)
            delivery = delivery.WithSender(new HostedAddress(delivery.Sender, parentHub.Address));
        parentHub.DeliverMessage(delivery);
        return delivery.Forwarded();
    }
}

