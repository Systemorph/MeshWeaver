// <meshweaver>
// Id: Testing/MessagingHub/HostedHubTest
// DisplayName: Testing/MessagingHub/HostedHubTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
﻿using System.Threading;
using System.Threading.Tasks;

using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
public class HostedHubTest(MeshTestContext context) : InMeshTestBase(output)
{
    public record Ping : IRequest<Pong>;
    public record Pong;
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithTypes(typeof(Ping), typeof(Pong))
            .WithHandler<Ping>((hub, request) =>
            {
                hub.Post(new Pong(), o => o.ResponseFor(request));
                return request.Processed();
            });
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration);
    }

    [MeshFact]
    public async Task HostedPingPong()
    {
        var client = GetClient();
        var subHub =
            client.ServiceProvider.CreateMessageHub(new Address("new", "1"),
                // Plumbing fixture with no user → posts as infrastructure (System), per the
                // never-null AccessContext invariant (feedback_access_context_always_set).
                conf => conf.WithTypes(typeof(Ping), typeof(Pong))
                    .WithPostingIdentity(PostingIdentity.System)
                );
        var response = await subHub
            .Observe(new Ping(), o => o.WithTarget(CreateHostAddress())).Should().Within(5.Seconds()).Emit();
        response.Message.Should().BeOfType<Pong>();
    }

}
