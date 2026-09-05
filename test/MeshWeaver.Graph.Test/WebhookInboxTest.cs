#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the generic webhook inbox (<see cref="WebhookInbox.Deliver"/>): fail-closed on the target
/// allowlist AND on target-node existence (a satellite must anchor under a real owner — an
/// ownerless satellite NotFound-storms the router), size-capped, credential headers stripped while
/// signature headers survive verbatim, and the accepted delivery lands as a
/// <see cref="WebhookEvent"/> node at <c>{target}/_Inbox/{id}</c>.
///
/// <para>🚨 And #3312: a target that declares <see cref="WebhookInbox.SecretConfigKeyName"/>
/// has its HMAC verified HERE, so the ANSWER carries the verdict. The pair that matters is
/// <see cref="SignedTarget_WithTheRightSecret_IsAccepted"/> versus
/// <see cref="SignedTarget_WithADriftedSecret_IsRefused_AndStoresNothing"/>: both were
/// <c>Accepted</c> before the fix, which is exactly what made a mismatched secret invisible. If a
/// change makes them agree again, the hole is back.</para>
/// </summary>
public class WebhookInboxTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The secret the instance holds, under the key a signed target names.</summary>
    private const string SecretKey = "Test:PlatformWebhookSecret";
    private const string InstanceSecret = "s3cr3t-on-the-instance";

    /// <summary>A key the instance declares but never provisions — the fail-closed shape.</summary>
    private const string UnprovisionedKey = "Test:NeverProvisioned";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddWebhookInbox()
            .ConfigureServices(services => services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [SecretKey] = InstanceSecret,
                    [UnprovisionedKey] = "",
                }).Build()));

    /// <summary>The GitHub-style header a sender computes with <paramref name="secret"/>.</summary>
    private static KeyValuePair<string, string> Sign(string body, string secret)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(secret));
        var hex = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant();
        return new(WebhookInbox.SignatureHeader, $"sha256={hex}");
    }

    private Task<WebhookInbox.DeliveryResult> Post(
        WebhookInbox.WebhookTarget allowed, string target,
        IEnumerable<KeyValuePair<string, string>> headers, string body) =>
        WebhookInbox.Deliver(Mesh, [allowed], target, "application/json", headers, body)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await();

    private async Task<int> InboxCount(string target) =>
        (await Observable.Using(
                () => Access.ImpersonateAsSystem(),
                _ => MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                        $"path:{target}/{WebhookInbox.InboxContainer} scope:children"))
                    .Take(1))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await())
        .Items.Count(n => n.NodeType == WebhookInbox.NodeType);

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private Task<MeshNode> WriteAsSystem(MeshNode node) =>
        Observable.Using(
                () => Access.ImpersonateAsSystem(),
                _ => MeshService.CreateOrUpdateNode(node))
            .FirstAsync().Await();

    private Task<MeshNode?> Find(string path) =>
        Observable.Using(
                () => Access.ImpersonateAsSystem(),
                _ => MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}")).Take(1)
                    .Select(c => c.Items.FirstOrDefault(n => n.Path == path)))
            .FirstAsync().Await();

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
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await();

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
                .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await())
            .Status.Should().Be(WebhookInbox.DeliveryStatus.UnknownTarget);

        // Allowlisted but no node at the path → refused (the satellite would be ownerless).
        (await WebhookInbox.Deliver(Mesh, ["Ghost"], "Ghost", null, [], "{}")
                .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await())
            .Status.Should().Be(WebhookInbox.DeliveryStatus.UnknownTarget);

        // Path-shape games never resolve to an allowlisted target.
        (await WebhookInbox.Deliver(Mesh, ["Existing"], "Existing/../Other", null, [], "{}")
                .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await())
            .Status.Should().Be(WebhookInbox.DeliveryStatus.UnknownTarget);

        // Slash normalization DOES resolve ("/Existing/" ≡ "Existing").
        (await WebhookInbox.Deliver(Mesh, ["Existing"], "/Existing/", null, [], "{}")
                .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await())
            .Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
    }

    [Fact(Timeout = 120000)]
    public async Task OversizedBodies_AreRefused()
    {
        await CreateTarget("Sized");
        var huge = new string('x', WebhookInbox.MaxBodyBytes + 1);
        (await WebhookInbox.Deliver(Mesh, ["Sized"], "Sized", null, [], huge)
                .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await())
            .Status.Should().Be(WebhookInbox.DeliveryStatus.TooLarge);
    }

    [Fact(Timeout = 120000)]
    public async Task EveryDeliveryGetsItsOwnNode()
    {
        await CreateTarget("Multi");
        var first = await WebhookInbox.Deliver(Mesh, ["Multi"], "Multi", null, [], "{\"n\":1}")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await();
        var second = await WebhookInbox.Deliver(Mesh, ["Multi"], "Multi", null, [], "{\"n\":2}")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).Await();
        first.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
        second.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
        second.NodePath.Should().NotBe(first.NodePath);
    }

    // ───────────────────────── #3312: the answer carries the verdict ─────────────────────────

    /// <summary>The build fact CD signs and posts.</summary>
    private const string BuildFact = "{\"event\":\"platform-build\",\"digest\":\"sha256:abc\"}";

    [Fact(Timeout = 120000)]
    public async Task SignedTarget_WithTheRightSecret_IsAccepted()
    {
        await CreateTarget("Signed");

        var result = await Post(
            new WebhookInbox.WebhookTarget("Signed", SecretKey), "Signed",
            [Sign(BuildFact, InstanceSecret)], BuildFact);

        result.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
        (await InboxCount("Signed")).Should().Be(1);
    }

    /// <summary>
    /// 🚨 THE DEFECT, as a test. Same target, same body, a secret that drifted by one
    /// character: before #3312 this returned <c>Accepted</c> — byte-identical to the test above —
    /// and the delivery was stored, answered 2xx, then silently dropped by the consumer as
    /// unverifiable, with nothing anywhere red. The two assertions are the whole fix: a distinct
    /// verdict, and NOTHING left behind.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task SignedTarget_WithADriftedSecret_IsRefused_AndStoresNothing()
    {
        await CreateTarget("Drifted");

        var result = await Post(
            new WebhookInbox.WebhookTarget("Drifted", SecretKey), "Drifted",
            [Sign(BuildFact, InstanceSecret + "x")], BuildFact);

        result.Status.Should().Be(WebhookInbox.DeliveryStatus.SignatureInvalid);
        result.NodePath.Should().BeNull();
        (await InboxCount("Drifted")).Should().Be(0,
            "a delivery that fails to verify must leave nothing behind — otherwise the endpoint "
            + "has only moved the silent drop from the consumer into the store");
    }

    [Fact(Timeout = 120000)]
    public async Task SignedTarget_RefusesAnAbsentOrMalformedOrRebodiedSignature()
    {
        await CreateTarget("Malformed");
        var target = new WebhookInbox.WebhookTarget("Malformed", SecretKey);

        // No signature header at all — the shape an unaware sender produces.
        (await Post(target, "Malformed", [], BuildFact))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.SignatureInvalid);

        // Present, but not the scheme this endpoint verifies.
        (await Post(target, "Malformed",
                [new(WebhookInbox.SignatureHeader, "t=1,v1=abc")], BuildFact))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.SignatureInvalid);

        // Correctly signed with the right secret — over a DIFFERENT body than the one delivered.
        (await Post(target, "Malformed", [Sign("{}", InstanceSecret)], BuildFact))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.SignatureInvalid);

        (await InboxCount("Malformed")).Should().Be(0);
    }

    /// <summary>
    /// A declared key that resolves to nothing refuses EVERYTHING — including a delivery signed
    /// with the empty string, which is what "verify against whatever is there" would have let
    /// through. A key present with an empty value is the config shape no guard can see, so it must
    /// fail in the loud direction rather than degrade to accepting.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task DeclaredButUnprovisionedSecret_RefusesEverything_FailClosed()
    {
        await CreateTarget("NoSecret");
        var target = new WebhookInbox.WebhookTarget("NoSecret", UnprovisionedKey);

        (await Post(target, "NoSecret", [Sign(BuildFact, "")], BuildFact))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.SecretUnavailable);
        (await Post(target, "NoSecret", [Sign(BuildFact, InstanceSecret)], BuildFact))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.SecretUnavailable);

        (await InboxCount("NoSecret")).Should().Be(0);
    }

    /// <summary>
    /// The dumb contract survives: a target that declares NO key stores whatever arrives, garbage
    /// signature and all, because schemes this endpoint does not speak (Stripe) stay the
    /// consumer's job. Without this, adding verification would have broken every unsigned
    /// integration on the mesh.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task TargetWithoutADeclaredSecret_KeepsTheDumbContract()
    {
        await CreateTarget("Dumb");

        var result = await Post(new WebhookInbox.WebhookTarget("Dumb"), "Dumb",
            [new(WebhookInbox.SignatureHeader, "sha256=deadbeef")], "{}");

        result.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
        (await InboxCount("Dumb")).Should().Be(1);
    }

    /// <summary>
    /// The declaration rides ON the allowlist entry, so <c>Targets:N</c> still reads as a plain
    /// path for every consumer that projects <c>c.Value</c> — core's own broadcaster and, out of
    /// this compiler's sight entirely, the in-mesh <c>PaymentPathAudit</c> in MeshWeaver.Plugins.
    /// A parallel section would have been a second copy of the same graph, free to drift; this
    /// asserts it is not one.
    /// </summary>
    [Fact]
    public void ReadTargets_ParsesTheDeclaration_WithoutDisturbingThePlainPathReading()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [$"{WebhookInbox.TargetsConfigSection}:0"] = "Hosting/PlatformBuilds",
                [$"{WebhookInbox.TargetsConfigSection}:0:{WebhookInbox.SecretConfigKeyName}"] =
                    "Hosting:PlatformWebhookSecret",
                [$"{WebhookInbox.TargetsConfigSection}:1"] = "Store/Payments",
                [$"{WebhookInbox.TargetsConfigSection}:2"] = "",
            }).Build();

        var targets = WebhookInbox.ReadTargets(configuration);

        targets.Select(t => t.Path).Should().Equal("Hosting/PlatformBuilds", "Store/Payments");
        targets[0].SecretConfigKey.Should().Be("Hosting:PlatformWebhookSecret");
        targets[1].SecretConfigKey.Should().BeNull("an unsigned target declares nothing");

        // The legacy projection every other reader still uses sees exactly what it saw before.
        configuration.GetSection(WebhookInbox.TargetsConfigSection).GetChildren()
            .Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v))
            .Should().Equal("Hosting/PlatformBuilds", "Store/Payments");
    }
}
