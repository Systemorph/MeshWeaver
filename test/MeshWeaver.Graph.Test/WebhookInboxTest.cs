#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the generic webhook inbox (<see cref="WebhookInbox.Deliver"/>): fail-closed on the target
/// allowlist AND on target-node existence (a satellite must anchor under a real owner — an
/// ownerless satellite NotFound-storms the router), size-capped, credential headers stripped while
/// signature headers survive verbatim, and the accepted delivery lands as a
/// <see cref="WebhookEvent"/> node at <c>{target}/_Inbox/{id}</c>.
/// </summary>
public class WebhookInboxTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddWebhookInbox();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private Task<MeshNode> WriteAsSystem(MeshNode node) =>
        Observable.Using(
                () => Access.ImpersonateAsSystem(),
                _ => MeshService.CreateOrUpdateNode(node))
            .FirstAsync().ToTask();

    private Task<MeshNode?> Find(string path) =>
        Observable.Using(
                () => Access.ImpersonateAsSystem(),
                _ => MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}")).Take(1)
                    .Select(c => c.Items.FirstOrDefault(n => n.Path == path)))
            .FirstAsync().ToTask();

    private Task<MeshNode> CreateTarget(string path) =>
        WriteAsSystem(new MeshNode(path)
        {
            Name = path,
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# Payments\n" },
        });

    private static readonly IReadOnlyList<KeyValuePair<string, string>> StripeHeaders =
    [
        new("Stripe-Signature", "t=1700000000,v1=abc123"),
        new("Content-Type", "application/json"),
        new("Authorization", "Bearer leaked-should-be-dropped"),
        new("Cookie", "session=nope"),
    ];

    [Fact(Timeout = 120000)]
    public async Task AllowlistedExistingTarget_StoresTheDelivery_WithSignatureButNoCredentials()
    {
        await CreateTarget("Payments");

        var result = await WebhookInbox.Deliver(
                Mesh, ["Payments"], "Payments", "application/json", StripeHeaders,
                """{"type":"checkout.session.completed"}""")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

        result.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
        result.NodePath.Should().StartWith($"Payments/{WebhookInbox.InboxContainer}/");

        var stored = await Find(result.NodePath!);
        stored.Should().NotBeNull();
        stored!.NodeType.Should().Be(WebhookInbox.NodeType);
        stored.MainNode.Should().Be("Payments");
        var content = stored.ContentAs<WebhookEvent>(Mesh.JsonSerializerOptions)!;
        content.Body.Should().Contain("checkout.session.completed");
        content.ContentType.Should().Be("application/json");
        content.Headers["Stripe-Signature"].Should().Be("t=1700000000,v1=abc123",
            "the consumer verifies authenticity over the verbatim signature header");
        content.Headers.ContainsKey("Authorization").Should().BeFalse("credentials are never persisted");
        content.Headers.ContainsKey("Cookie").Should().BeFalse("credentials are never persisted");
    }

    [Fact(Timeout = 120000)]
    public async Task TargetsNotAllowlisted_OrWithoutAnOwnerNode_AreRefused()
    {
        await CreateTarget("Existing");

        // Exists but not allowlisted → refused.
        (await WebhookInbox.Deliver(Mesh, [], "Existing", null, [], "{}")
                .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask())
            .Status.Should().Be(WebhookInbox.DeliveryStatus.UnknownTarget);

        // Allowlisted but no node at the path → refused (the satellite would be ownerless).
        (await WebhookInbox.Deliver(Mesh, ["Ghost"], "Ghost", null, [], "{}")
                .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask())
            .Status.Should().Be(WebhookInbox.DeliveryStatus.UnknownTarget);

        // Path-shape games never resolve to an allowlisted target.
        (await WebhookInbox.Deliver(Mesh, ["Existing"], "Existing/../Other", null, [], "{}")
                .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask())
            .Status.Should().Be(WebhookInbox.DeliveryStatus.UnknownTarget);

        // Slash normalization DOES resolve ("/Existing/" ≡ "Existing").
        (await WebhookInbox.Deliver(Mesh, ["Existing"], "/Existing/", null, [], "{}")
                .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask())
            .Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
    }

    [Fact(Timeout = 120000)]
    public async Task OversizedBodies_AreRefused()
    {
        await CreateTarget("Sized");
        var huge = new string('x', WebhookInbox.MaxBodyBytes + 1);
        (await WebhookInbox.Deliver(Mesh, ["Sized"], "Sized", null, [], huge)
                .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask())
            .Status.Should().Be(WebhookInbox.DeliveryStatus.TooLarge);
    }

    [Fact(Timeout = 120000)]
    public async Task EveryDeliveryGetsItsOwnNode()
    {
        await CreateTarget("Multi");
        var first = await WebhookInbox.Deliver(Mesh, ["Multi"], "Multi", null, [], "{\"n\":1}")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
        var second = await WebhookInbox.Deliver(Mesh, ["Multi"], "Multi", null, [], "{\"n\":2}")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
        first.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
        second.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
        second.NodePath.Should().NotBe(first.NodePath);
    }
}
