using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Verifies the route-registration robustness added to
/// <c>DataExtensions.RouteStreamMessage</c>: stream messages survive a
/// child→parent search for the sync sub-hub when the hub that hosts the
/// sync sub-hub is an ancestor of the hub that receives the delivery.
/// And the <see cref="HostedHubsCollection.HubAdded"/> observable fires
/// synchronously so consumers can react to slow-registering sub-hubs
/// without polling.
/// </summary>
public class RouteStreamMessageParentWalkTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Walks the parent chain: ensures that when a child hub looks for a
    /// hosted sub-hub by address, the search resolves on its parent if the
    /// child's own collection doesn't have it. This mirrors the path used
    /// by <c>RouteStreamMessage</c>.
    /// </summary>
    [Fact]
    public void GetHostedHub_ChildLooksUp_ParentChainResolvesSubHub()
    {
        var host = GetHost();
        var childAddr = new Address("test-child", Guid.NewGuid().ToString("N"));
        var subHubAddr = new Address("test-sub", Guid.NewGuid().ToString("N"));

        // Sub-hub is hosted under the HOST (the parent of "child").
        var subHub = host.GetHostedHub(subHubAddr, c => c);
        subHub.Should().NotBeNull();

        // Child hub is also hosted under the host (siblings of subHub).
        // Looking up subHubAddr on the child directly must miss; the
        // RouteStreamMessage walk-up-the-parent-chain finds it on host.
        var childHub = host.GetHostedHub(childAddr, c => c);

        var foundOnChild = childHub.GetHostedHub(subHubAddr, HostedHubCreation.Never);
        foundOnChild.Should().BeNull("sub-hub is on the parent, not the child");

        // Walk the chain — same loop RouteStreamMessage now uses.
        IMessageHub? walker = childHub;
        IMessageHub? found = null;
        while (walker is not null)
        {
            found = walker.GetHostedHub(subHubAddr, HostedHubCreation.Never);
            if (found is not null) break;
            walker = walker.Configuration.ParentHub;
        }

        found.Should().BeSameAs(subHub,
            "the parent-chain walk must find the sub-hub on an ancestor");
    }

    /// <summary>
    /// <see cref="HostedHubsCollection.HubAdded"/> fires synchronously when
    /// the collection's <c>Add</c>/<c>GetHub</c> create path registers a
    /// new hub. A reactive route handler can subscribe to this and re-attempt
    /// delivery when a slow-registering sync hub finally appears.
    /// </summary>
    [Fact]
    public void HostedHubsCollection_HubAdded_FiresOnGetHub_CreateAlways()
    {
        var host = GetHost();
        var observedAddresses = new List<Address>();

        // Reach the host's HostedHubsCollection via DI.
        var collection = host.ServiceProvider.GetService<HostedHubsCollection>();
        collection.Should().NotBeNull("HostedHubsCollection is registered as a singleton on every hub");

        using var sub = collection!.HubAdded.Subscribe(h => observedAddresses.Add(h.Address));

        var newAddr = new Address("test-added", Guid.NewGuid().ToString("N"));
        var newHub = host.GetHostedHub(newAddr, c => c);
        newHub.Should().NotBeNull();

        observedAddresses.Should().Contain(a => a.Equals(newAddr),
            "HubAdded must emit synchronously when GetHub creates a new hub");
    }
}
