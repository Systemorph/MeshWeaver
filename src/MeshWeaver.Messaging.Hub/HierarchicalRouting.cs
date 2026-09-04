using System.Reactive.Linq;
using MeshWeaver.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    /// Offers a correlated reply this router is about to DROP to a local waiter
    /// (<see cref="IUndeliverableReplySink"/>). Only a delivery carrying
    /// <see cref="PostOptions.RequestId"/> qualifies — fire-and-forget traffic has nobody waiting,
    /// and answering it is the storm shape every NACK guard here exists to avoid.
    /// </summary>
    /// <returns><c>true</c> when a waiter took it, so the sender HAS its answer.</returns>
    private bool TryHandOverUndeliverableReply(IMessageDelivery delivery)
    {
        if (!delivery.Properties.ContainsKey(PostOptions.RequestId))
            return false;
        try
        {
            var sink = hub.ServiceProvider.GetService<IUndeliverableReplySink>();
            if (sink is null || !sink.TryDeliver(delivery))
                return false;
            logger.LogDebug(
                "Undeliverable {MessageType} (ID: {MessageId}) for request {RequestId} was handed to "
                + "a local waiter instead of being dropped in {Address}",
                delivery.Message?.GetType().Name, delivery.Id,
                delivery.Properties[PostOptions.RequestId], hub.Address);
            return true;
        }
        catch (Exception ex)
        {
            // The hand-over is a last resort; a sink that throws must not turn a classified drop
            // into an unhandled fault on the routing path.
            logger.LogWarning(ex,
                "The undeliverable-reply sink threw for {MessageType} (ID: {MessageId}) in {Address}; "
                + "the delivery is dropped as before",
                delivery.Message?.GetType().Name, delivery.Id, hub.Address);
            return false;
        }
    }

    /// <summary>
    /// Loops through forward rules in a sequence. Each forward rule either applies and returns delivery.Forwarded() or doesn't apply and returns delivery.
    /// </summary>
    /// <param name="delivery"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public IMessageDelivery RouteMessageAsync(IMessageDelivery delivery,
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

        // The routing handlers are Observable.Return-shaped (synchronous emit) — fold
        // them in sequence by subscribing inline; each handler observes the prior result.
        foreach (var handler in configuration.Handlers)
        {
            var routed = delivery;
            handler(routed, cancellationToken).Subscribe(d => delivery = d);
        }

        if (delivery.State != MessageDeliveryState.Submitted)
            return delivery;

        // Check if we're at the target hub
        // Compare ignoring the Host part - the inner address is what matters for determining if we're at the target
        if (delivery.Target is null)
            return delivery;

        var targetWithoutHost = delivery.Target with { Host = null };
        if (targetWithoutHost.Equals(hub.Address))
            return delivery;

        return RouteAlongHostingHierarchy(delivery);
    }

    private IMessageDelivery RouteAlongHostingHierarchy(IMessageDelivery delivery)
    {
        if (delivery.Target is null)
            return delivery;


        var isDisposing = hub.RunLevel >= MessageHubRunLevel.DisposeHostedHubs;
        // [CanBeIgnored] messages (Shutdown/Dispose/HeartBeat) have no sender awaiting a
        // response, so a "no route" DeliveryFailure for them is meaningless AND it feeds the
        // DeliveryFailure⟷ShutdownRequest disposal ping-pong storm (see ReportFailure in
        // MessageService): a ShutdownRequest routed to an already-gone hub returns NotFound,
        // the DeliveryFailure routes back, and the pair spins until quiesce.
        var isFireAndForgetControl =
            delivery.Message.GetType().HasAttribute<CanBeIgnoredAttribute>();
        if (delivery.Target.Host != null)
        {
            var hosted = delivery.Target;
            // Per-routed-message; gate to skip GetType().Name + boxing.
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Routing delivery {id} of type {type} to host with address {target}", delivery.Id,
                    delivery.Message.GetType().Name, hosted.Host);
            if (hub.Address.Equals(hosted.Host))
            {
                // Get the inner address (the target without its host)
                var nextLevelAddress = hosted with { Host = null };
                // If the inner address also has a host, route to that host first
                if (nextLevelAddress.Host != null)
                    nextLevelAddress = nextLevelAddress.Host;

                // During disposal, only look for existing hubs, don't create new ones
                var creation = isDisposing ? HostedHubCreation.Never : HostedHubCreation.Always;
                var hostedHub = hub.GetHostedHub(nextLevelAddress, x => x, creation);

                if (hostedHub is not null)
                {
                    hostedHub.DeliverMessage(delivery.WithTarget(hosted with { Host = null }));
                    return delivery.Forwarded();
                }

                var errorMessage = isDisposing
                    ? $"No existing route found for host {hosted} and hub {hub.Address} is disposing"
                    : $"No route found for host {hosted}. Last tried in {hub.Address}";

                logger.LogDebug(errorMessage);
                if (!isDisposing && !isFireAndForgetControl)
                {
                    hub.Post(
                        new DeliveryFailure(delivery)
                        {
                            ErrorType = ErrorType.NotFound,
                            Message = errorMessage
                        }, o => o.ResponseFor(delivery)
                    );
                }
                // While disposing no NACK is posted above, so the delivery leaves here classified
                // and UNANSWERED — MessageService owes the sender a DeliveryFailure (a bare
                // Failed(...) used to be dropped on the not-on-target path and the caller waited
                // forever). TRANSIENT (ShuttingDown): the address may reactivate on the next probe.
                if (isDisposing && TryHandOverUndeliverableReply(delivery))
                    return delivery.Processed();

                return isDisposing
                    ? delivery.Failed(errorMessage, ErrorType.ShuttingDown)
                    : delivery.NotFound();
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

            var firstTarget = delivery.Target;
            while (firstTarget.Host is not null)
                firstTarget = firstTarget.Host;
            var hosted = hub.GetHostedHub(firstTarget, create: HostedHubCreation.Never);
            if (hosted is not null)
            {
                hosted.DeliverMessage(delivery);
                return delivery.Forwarded(hosted.Address);
            }
            var errorMessage = isDisposing
                ? $"No route found for {delivery.Target} and hub {hub.Address} is disposing"
                : $"No route found for host {delivery.Target}. Last tried in {hub.Address}";

            logger.LogDebug(errorMessage);
            if (!isDisposing && !isFireAndForgetControl)
            {
                hub.Post(
                    new DeliveryFailure(delivery)
                    {
                        ErrorType = ErrorType.NotFound,
                        Message = errorMessage
                    }, o => o.ResponseFor(delivery)
                );
            }
            // Same contract as the hosted-route branch above: the disposing arm posted no NACK, so
            // it leaves CLASSIFIED and UNANSWERED for MessageService to report.
            return isDisposing
                ? delivery.Failed(errorMessage, ErrorType.ShuttingDown)
                : delivery.NotFound();
        }

        // Check if parent hub is also disposing before routing up.
        //
        // 🚨 Nothing has answered the sender at this point and nothing downstream will unless the
        // failure is CLASSIFIED: a bare Failed(...) here is not on-target, so MessageService's
        // routing tail used to return it unreported and the requester's hub.Observe(...) waited
        // indefinitely. This is the teardown shape from #981 — a hosted hub quiescing AFTER its
        // parent reached DisposeHostedHubs still posts to that parent, and the reply can never come.
        // TRANSIENT (ShuttingDown), never terminal: the parent may reactivate, and long-lived
        // consumers (SynchronizationStream's resubscribe latch) must ride it out rather than die.
        if (parentHub.RunLevel >= MessageHubRunLevel.DisposeHostedHubs)
        {
            logger.LogDebug("Cannot route to parent hub {ParentAddress} - parent is also disposing. Message: {MessageType}",
                parentHub.Address, delivery.Message.GetType().Name);
            // 🚨 The wording carries the "is shutting down" marker on purpose. Three classifiers —
            // MeshNodeStreamCache.IsTransientOwnerFailure, AreaErrorClassifier.IsTransientHubFailure
            // and SynchronizationStream's transient check — still recognise a transient hub reject by
            // that substring, so a NACK phrased any other way would be filed as a PERMANENT owner
            // failure and cached as one.
            // 🚨 …but if this is a REPLY somebody HERE is waiting for, hand it over before dropping
            // it. The owner answered correctly and its post was accepted; only the route out died,
            // and the waiter is in this same process with its registry entry armed. See
            // IUndeliverableReplySink — offered ONLY here, where no post can reach the caller any
            // more, never alongside a healthy one.
            if (TryHandOverUndeliverableReply(delivery))
                return delivery.Processed();

            return delivery.Failed(
                $"Hub {hub.Address} cannot route {delivery.Message.GetType().Name} to {delivery.Target} — "
                + $"its parent hub {parentHub.Address} is shutting down (RunLevel={parentHub.RunLevel}). "
                + "The address may reactivate (recycle / restart); retry to get the authoritative answer.",
                ErrorType.ShuttingDown);
        }

        // Per-routed-message; gate to skip GetType().Name + boxing.
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Routing delivery {id} of type {type} to parent {target}", delivery.Id,
                delivery.Message.GetType().Name, parentHub.Address);
        if (parentHub.Address.Type != AddressExtensions.MeshType)
            delivery = delivery.WithSender(delivery.Sender.WithHost(parentHub.Address));
        parentHub.DeliverMessage(delivery);
        return delivery.Forwarded();
    }
}

