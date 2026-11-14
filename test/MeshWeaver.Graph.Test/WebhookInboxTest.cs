#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics.Metrics;
using System.Collections.Immutable;
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
                    [$"{SecretKey}:{SenderId}"] = SenderSecret,
                    [$"{SecretKey}:blank"] = "",
                }).Build()));

    /// <summary>A sender holding its OWN key under the shared one (Plugins#1913).</summary>
    private const string SenderId = "fabrikam";
    private const string SenderSecret = "fabrikams-own-announcement-key";

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
        IEnumerable<KeyValuePair<string, string>> headers, string body,
        CancellationToken cancellationToken = default) =>
        WebhookInbox.Deliver(Mesh, [allowed], target, "application/json", headers, body)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(cancellationToken);

    private async Task<int> InboxCount(string target, CancellationToken cancellationToken = default) =>
        (await Observable.Using(
                () => Access.ImpersonateAsSystem(),
                _ => MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                        $"path:{target}/{WebhookInbox.InboxContainer} scope:children"))
                    .Take(1))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(cancellationToken))
        .Items.Count(n => n.NodeType == WebhookInbox.NodeType);

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private Task<MeshNode> WriteAsSystem(MeshNode node, CancellationToken cancellationToken = default) =>
        Observable.Using(
                () => Access.ImpersonateAsSystem(),
                _ => MeshService.CreateOrUpdateNode(node))
            .FirstAsync().Await(cancellationToken);

    private Task<MeshNode?> Find(string path) =>
        Observable.Using(
                () => Access.ImpersonateAsSystem(),
                _ => MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}")).Take(1)
                    .Select(c => c.Items.FirstOrDefault(n => n.Path == path)))
            .FirstAsync().Await(TestContext.Current.CancellationToken);

    private Task<MeshNode> CreateTarget(string path, CancellationToken cancellationToken = default) =>
        WriteAsSystem(new MeshNode(path)
        {
            Name = path,
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# Payments\n" },
        }, cancellationToken);

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
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

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

    /// <summary>
    /// 🚨 The #1790 invariant, pinned on the ONE in-process caller of this method that is not an
    /// HTTP request thread (the self-update hand-over delivering into the control instance's own
    /// inbox, MeshWeaver#4098): the write runs as System — an anonymous or ordinary caller has no
    /// write access anywhere, so a delivery that landed proves the impersonation happened — and the
    /// caller's OWN ambient identity is exactly what it was once <c>Subscribe</c> returns, and under
    /// every notification. The former <c>Observable.Using(ImpersonateAsSystem, …)</c> shape passes
    /// every other assertion in this class and fails these two: it opened the scope on the
    /// subscribing thread and disposed it wherever the create's response landed, leaving the
    /// subscriber latched as <c>system-security</c> for everything it did next.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AnInProcessCaller_KeepsItsOwnIdentity_WhileTheWriteRunsAsSystem()
    {
        await CreateTarget("InProcess");
        var caller = new AccessContext { ObjectId = "user/in-process-caller", Name = "in-process caller" };

        AccessContext? seenOnNotification = null;
        AccessContext? afterSubscribe;
        Task<WebhookInbox.DeliveryResult> pending;
        using (Access.SwitchAccessContext(caller))
        {
            // Await() subscribes synchronously, INSIDE the caller's scope — as the poller's pool
            // thread does — so the ambient read right after it is the one the scope hands back.
            pending = WebhookInbox.Deliver(Mesh, ["InProcess"], "InProcess", "application/json", [], "{\"n\":1}")
                .Do(_ => seenOnNotification = Access.Context)
                .FirstAsync().Timeout(TestTimeouts.Convergence)
                .Await(TestContext.Current.CancellationToken);
            afterSubscribe = Access.Context;
        }
        var result = await pending;

        result.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted,
            "the event node is created as System — a caller with no write access anywhere could not have stored it");
        (await Find(result.NodePath!)).Should().NotBeNull();
        afterSubscribe.Should().NotBeNull();
        afterSubscribe!.ObjectId.Should().Be(caller.ObjectId,
            "the scope must be left on the way out of the same Subscribe that opened it — a latched "
            + "System identity on the subscribing thread is the #1790 defect");
        seenOnNotification.Should().NotBeNull();
        seenOnNotification!.ObjectId.Should().Be(caller.ObjectId,
            "every notification is delivered under the subscriber's own identity, never System");
    }

    /// <summary>
    /// The consumer drains its inbox from its OWN hub, so a stored delivery must ACTIVATE the owner:
    /// a committed satellite alone does not, and a verified delivery then waited until somebody
    /// opened the node (MeshWeaver.Plugins#5435, 140 runs queued overnight on the public instance).
    /// Precondition: creating the owner activates nothing; then the delivery builds the owner's hub.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AStoredDelivery_WakesItsOwnersHub()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateTarget("WakeMe", ct);
        var address = new Address("WakeMe");
        var hosted = Mesh.ServiceProvider.GetRequiredService<HostedHubsCollection>();
        var built = hosted.HubAdded.Where(h => (h.Address with { Host = null }) == address).Replay();
        using (built.Connect())
        {
            var result = await WebhookInbox.Deliver(
                    Mesh, ["WakeMe"], "WakeMe", "application/json", [], """{"wake":true}""")
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            result.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);

            await built.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            Mesh.GetHostedHub(address, HostedHubCreation.Never).Should().NotBeNull(
                "the delivery wakes the owner, where the consumer's inbox watch is armed");
        }
    }

    [Fact(Timeout = 120000)]
    public async Task TargetsNotAllowlisted_OrWithoutAnOwnerNode_AreRefused()
    {
        await CreateTarget("Existing");

        // Exists but not allowlisted → refused.
        (await WebhookInbox.Deliver(Mesh, [], "Existing", null, [], "{}")
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.UnknownTarget);

        // Allowlisted but no node at the path → refused (the satellite would be ownerless).
        (await WebhookInbox.Deliver(Mesh, ["Ghost"], "Ghost", null, [], "{}")
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.UnknownTarget);

        // ... and for a kind the target does not accept, too: the owner is checked BEFORE the
        // accepted-events filter, so an ignored kind never answers 2xx where a stored one is refused.
        (await WebhookInbox.Deliver(Mesh,
                    [new WebhookInbox.WebhookTarget("Ghost") { AcceptedEvents = ["pull_request"] }], "Ghost", null,
                    [new KeyValuePair<string, string>(WebhookInbox.GitHubEventHeader, "check_run")], "{}")
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.UnknownTarget);

        // Path-shape games never resolve to an allowlisted target.
        (await WebhookInbox.Deliver(Mesh, ["Existing"], "Existing/../Other", null, [], "{}")
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.UnknownTarget);

        // Slash normalization DOES resolve ("/Existing/" ≡ "Existing").
        (await WebhookInbox.Deliver(Mesh, ["Existing"], "/Existing/", null, [], "{}")
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
    }

    [Fact(Timeout = 120000)]
    public async Task OversizedBodies_AreRefused()
    {
        await CreateTarget("Sized");
        var huge = new string('x', WebhookInbox.MaxBodyBytes + 1);
        (await WebhookInbox.Deliver(Mesh, ["Sized"], "Sized", null, [], huge)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.TooLarge);
    }

    [Fact(Timeout = 120000)]
    public async Task EveryDeliveryGetsItsOwnNode()
    {
        await CreateTarget("Multi");
        var first = await WebhookInbox.Deliver(Mesh, ["Multi"], "Multi", null, [], "{\"n\":1}")
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        var second = await WebhookInbox.Deliver(Mesh, ["Multi"], "Multi", null, [], "{\"n\":2}")
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
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
        await CreateTarget("Signed", TestContext.Current.CancellationToken);

        var result = await Post(
            new WebhookInbox.WebhookTarget("Signed", SecretKey), "Signed",
            [Sign(BuildFact, InstanceSecret)], BuildFact, TestContext.Current.CancellationToken);

        result.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
        result.SignatureVerified.Should().BeTrue(
            "the ANSWER, not the status, is what a signing publisher reads — this is the flag the "
            + "endpoint renders as `\"signature\":\"verified\"` and both CD lanes judge (#3338)");
        (await InboxCount("Signed", TestContext.Current.CancellationToken)).Should().Be(1);
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
        await CreateTarget("Drifted", TestContext.Current.CancellationToken);

        var result = await Post(
            new WebhookInbox.WebhookTarget("Drifted", SecretKey), "Drifted",
            [Sign(BuildFact, InstanceSecret + "x")], BuildFact, TestContext.Current.CancellationToken);

        result.Status.Should().Be(WebhookInbox.DeliveryStatus.SignatureInvalid);
        result.NodePath.Should().BeNull();
        (await InboxCount("Drifted", TestContext.Current.CancellationToken)).Should().Be(0,
            "a delivery that fails to verify must leave nothing behind — otherwise the endpoint "
            + "has only moved the silent drop from the consumer into the store");
    }

    [Fact(Timeout = 120000)]
    public async Task SignedTarget_RefusesAnAbsentOrMalformedOrRebodiedSignature()
    {
        await CreateTarget("Malformed", TestContext.Current.CancellationToken);
        var target = new WebhookInbox.WebhookTarget("Malformed", SecretKey);

        // No signature header at all — the shape an unaware sender produces.
        (await Post(target, "Malformed", [], BuildFact, TestContext.Current.CancellationToken))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.SignatureInvalid);

        // Present, but not the scheme this endpoint verifies.
        (await Post(target, "Malformed",
                [new(WebhookInbox.SignatureHeader, "t=1,v1=abc")], BuildFact, TestContext.Current.CancellationToken))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.SignatureInvalid);

        // Correctly signed with the right secret — over a DIFFERENT body than the one delivered.
        (await Post(target, "Malformed", [Sign("{}", InstanceSecret)], BuildFact, TestContext.Current.CancellationToken))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.SignatureInvalid);

        (await InboxCount("Malformed", TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>
    /// 🚨 Plugins#1913: a sender with its OWN key (a child of the target's shared key) is stored
    /// and MARKED with that key's name, so the consumer can hold it to what that one sender may
    /// cause. The shared-secret delivery above carries no sender key — the pair is the whole point:
    /// if a change makes them agree, a per-deployment key is indistinguishable from the fleet secret
    /// again and the consumer's scoping is judging a constant.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task SignedTarget_WithASendersOwnKey_IsAccepted_AndNamesTheSender()
    {
        await CreateTarget("PerSender", TestContext.Current.CancellationToken);
        var target = new WebhookInbox.WebhookTarget("PerSender", SecretKey);

        var own = await Post(target, "PerSender",
            [Sign(BuildFact, SenderSecret)], BuildFact, TestContext.Current.CancellationToken);
        own.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
        own.SignatureVerified.Should().BeTrue();
        own.SenderKey.Should().Be(SenderId,
            "the consumer decides what a sender key may cause, so the inbox must say WHICH one verified");

        var shared = await Post(target, "PerSender",
            [Sign(BuildFact, InstanceSecret)], BuildFact, TestContext.Current.CancellationToken);
        shared.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
        shared.SenderKey.Should().BeNull("the target's own shared secret is not a sender key");

        (await InboxCount("PerSender", TestContext.Current.CancellationToken)).Should().Be(2);
    }

    /// <summary>
    /// A sender key widens NOTHING for a signature that verifies with no key: a drifted signature,
    /// one computed with the EMPTY string (which a blank child would admit if blank counted as a
    /// key), and a sender key's signature over a different body are all still refused, and nothing
    /// is stored.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task SenderKeys_AdmitNothingThatVerifiesWithNoKey()
    {
        await CreateTarget("PerSenderNo", TestContext.Current.CancellationToken);
        var target = new WebhookInbox.WebhookTarget("PerSenderNo", SecretKey);

        (await Post(target, "PerSenderNo", [Sign(BuildFact, SenderSecret + "x")], BuildFact,
                TestContext.Current.CancellationToken))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.SignatureInvalid);
        (await Post(target, "PerSenderNo", [Sign(BuildFact, "")], BuildFact,
                TestContext.Current.CancellationToken))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.SignatureInvalid);
        (await Post(target, "PerSenderNo", [Sign("{}", SenderSecret)], BuildFact,
                TestContext.Current.CancellationToken))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.SignatureInvalid);

        (await InboxCount("PerSenderNo", TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>The pure selector: which child verifies, blank children skipped, null when none.</summary>
    [Fact]
    public void SenderKeyOf_NamesTheChildThatVerifies()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["K"] = "shared",
                ["K:build"] = "builds-key",
                ["K:fabrikam"] = "fabrikams-key",
                ["K:empty"] = "",
            }).Build();

        WebhookInbox.SenderKeyOf(configuration, "K", Sign("{}", "fabrikams-key").Value, "{}")
            .Should().Be("fabrikam");
        WebhookInbox.SenderKeyOf(configuration, "K", Sign("{}", "builds-key").Value, "{}")
            .Should().Be("build");
        WebhookInbox.SenderKeyOf(configuration, "K", Sign("{}", "shared").Value, "{}")
            .Should().BeNull("the shared secret itself is the target's key, not a sender's");
        WebhookInbox.SenderKeyOf(configuration, "K", Sign("{}", "").Value, "{}")
            .Should().BeNull("a blank child is no key");
        WebhookInbox.SenderKeyOf(configuration, "K", null, "{}").Should().BeNull();

        // Two senders provisioned with the SAME value: the answer is an identity, so an ambiguous
        // match names nobody rather than whichever child the configuration lists first.
        var shared = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["K"] = "shared",
                ["K:build"] = "one-value-twice",
                ["K:fabrikam"] = "one-value-twice",
            }).Build();
        WebhookInbox.SenderKeyOf(shared, "K", Sign("{}", "one-value-twice").Value, "{}")
            .Should().BeNull("two senders sharing a key cannot be told apart, so neither is named");
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
        await CreateTarget("NoSecret", TestContext.Current.CancellationToken);
        var target = new WebhookInbox.WebhookTarget("NoSecret", UnprovisionedKey);

        (await Post(target, "NoSecret", [Sign(BuildFact, "")], BuildFact, TestContext.Current.CancellationToken))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.SecretUnavailable);
        (await Post(target, "NoSecret", [Sign(BuildFact, InstanceSecret)], BuildFact, TestContext.Current.CancellationToken))
            .Status.Should().Be(WebhookInbox.DeliveryStatus.SecretUnavailable);

        (await InboxCount("NoSecret", TestContext.Current.CancellationToken)).Should().Be(0);
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
        await CreateTarget("Dumb", TestContext.Current.CancellationToken);

        var result = await Post(new WebhookInbox.WebhookTarget("Dumb"), "Dumb",
            [new(WebhookInbox.SignatureHeader, "sha256=deadbeef")], "{}", TestContext.Current.CancellationToken);

        result.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
        result.SignatureVerified.Should().BeFalse(
            "an undeclared target is accepted having verified NOTHING — it renders as "
            + "`\"signature\":\"not-required\"`, and a publisher that signed must be able to tell "
            + "that apart from a delivery that WAS checked. This assertion and the one in "
            + "SignedTarget_WithTheRightSecret_IsAccepted are the pair: if a change makes them "
            + "agree, the two states are indistinguishable again and the #3338 escalation is "
            + "judging a constant");
        (await InboxCount("Dumb", TestContext.Current.CancellationToken)).Should().Be(1);
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

    // ── A declared intake filter (MeshWeaver#7863) ─────────────────────────────────────────────────

    private static KeyValuePair<string, string> GitHubEvent(string kind, string header = WebhookInbox.GitHubEventHeader) =>
        new(header, kind);

    private static WebhookInbox.WebhookTarget Declaring(string path, string? secretConfigKey, params string[] accepted) =>
        new(path, secretConfigKey)
        {
            AcceptedEvents = accepted.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
        };

    private const string CheckRunBody = "{\"action\":\"completed\",\"check_run\":{\"id\":1}}";
    private const string PullRequestBody = "{\"action\":\"opened\",\"pull_request\":{\"number\":7}}";

    /// <summary>
    /// 🚨 THE CHANGE: a kind the target does not declare is answered 2xx and NEVER written — no node,
    /// so nothing for the full-JSON, text and vector indexes to churn on — and it is COUNTED on the
    /// platform meter. The signature is still verified first.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task UnacceptedGitHubEvent_IsAnswered2xx_Counted_AndNeverWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateTarget("Filtered", ct);
        long counted = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == WebhookInboxMetrics.IgnoredCounterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "target" && Equals(tag.Value, "Filtered"))
                    Interlocked.Add(ref counted, value);
        });
        listener.Start();

        var result = await Post(
            Declaring("Filtered", SecretKey, "pull_request", "issues"), "Filtered",
            [Sign(CheckRunBody, InstanceSecret), GitHubEvent("check_run")], CheckRunBody, ct);

        result.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted,
            "the sender did nothing wrong — a refusal would make GitHub mark the hook failing");
        result.IgnoredEvent.Should().Be("check_run");
        result.NodePath.Should().BeNull("an ignored delivery is never written");
        result.SignatureVerified.Should().BeTrue("the filter runs after the signature verified");
        (await InboxCount("Filtered", ct)).Should().Be(0);
        Interlocked.Read(ref counted).Should().Be(1, "every ignored delivery is counted");
    }

    /// <summary>A declared kind is stored exactly as before — the header matched ignoring case,
    /// because HTTP/2 delivers it lowercased.</summary>
    [Fact(Timeout = 120000)]
    public async Task AcceptedGitHubEvent_IsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateTarget("FilteredAccepts", ct);

        var result = await Post(
            Declaring("FilteredAccepts", SecretKey, "pull_request", "issues"), "FilteredAccepts",
            [Sign(PullRequestBody, InstanceSecret), GitHubEvent("PULL_REQUEST", "x-github-event")], PullRequestBody, ct);

        result.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
        result.IgnoredEvent.Should().BeNull();
        result.NodePath.Should().StartWith($"FilteredAccepts/{WebhookInbox.InboxContainer}/");
        (await InboxCount("FilteredAccepts", ct)).Should().Be(1);
    }

    /// <summary>A target that declares no set keeps today's contract: every kind is stored.</summary>
    [Fact(Timeout = 120000)]
    public async Task TargetWithNoDeclaredSet_AcceptsEveryKind()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateTarget("Undeclared", ct);

        var result = await Post(
            new WebhookInbox.WebhookTarget("Undeclared", SecretKey), "Undeclared",
            [Sign(CheckRunBody, InstanceSecret), GitHubEvent("check_run")], CheckRunBody, ct);

        result.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
        result.IgnoredEvent.Should().BeNull();
        result.NodePath.Should().NotBeNull();
        (await InboxCount("Undeclared", ct)).Should().Be(1);
    }

    /// <summary>A delivery naming no GitHub event is not a GitHub delivery — the fleet's own signed
    /// envelopes — and a declared set never filters it.</summary>
    [Fact(Timeout = 120000)]
    public async Task DeliveryWithoutAGitHubEvent_IsWritten_OnADeclaringTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateTarget("FilteredEnvelope", ct);

        var result = await Post(
            Declaring("FilteredEnvelope", SecretKey, "pull_request"), "FilteredEnvelope",
            [Sign(BuildFact, InstanceSecret)], BuildFact, ct);

        result.Status.Should().Be(WebhookInbox.DeliveryStatus.Accepted);
        result.IgnoredEvent.Should().BeNull();
        (await InboxCount("FilteredEnvelope", ct)).Should().Be(1);
    }

    /// <summary>The filter never turns a 401 into a 2xx: an undeclared kind whose signature does
    /// not verify is still refused, so a drifted secret stays visible on every kind.</summary>
    [Fact(Timeout = 120000)]
    public async Task UnacceptedGitHubEvent_WithADriftedSecret_IsStillRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateTarget("FilteredDrifted", ct);

        var result = await Post(
            Declaring("FilteredDrifted", SecretKey, "pull_request"), "FilteredDrifted",
            [Sign(CheckRunBody, InstanceSecret + "x"), GitHubEvent("check_run")], CheckRunBody, ct);

        result.Status.Should().Be(WebhookInbox.DeliveryStatus.SignatureInvalid);
        result.IgnoredEvent.Should().BeNull();
    }

    /// <summary>The declaration rides on the allowlist entry, in both configuration shapes; a blank
    /// value is no declaration (accept all), never "accept nothing".</summary>
    [Fact]
    public void ReadTargets_ParsesTheAcceptedEvents_InBothShapes()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [$"{WebhookInbox.TargetsConfigSection}:0"] = "Hosting/PlatformBuilds",
                [$"{WebhookInbox.TargetsConfigSection}:0:{WebhookInbox.AcceptedEventsName}"] =
                    "pull_request, issues,pull_request_review",
                [$"{WebhookInbox.TargetsConfigSection}:1"] = "Hosting/Other",
                [$"{WebhookInbox.TargetsConfigSection}:1:{WebhookInbox.AcceptedEventsName}:0"] = "issues",
                [$"{WebhookInbox.TargetsConfigSection}:1:{WebhookInbox.AcceptedEventsName}:1"] = "push",
                [$"{WebhookInbox.TargetsConfigSection}:2"] = "Store/Payments",
                [$"{WebhookInbox.TargetsConfigSection}:2:{WebhookInbox.AcceptedEventsName}"] = "",
            }).Build();

        var targets = WebhookInbox.ReadTargets(configuration);

        targets[0].AcceptedEvents!.Order(StringComparer.Ordinal).Should().Equal("issues", "pull_request", "pull_request_review");
        targets[0].Accepts("Issues").Should().BeTrue();
        targets[0].Accepts("check_run").Should().BeFalse();
        targets[0].Accepts(null).Should().BeTrue("a delivery naming no GitHub event is never filtered");
        targets[1].AcceptedEvents!.Order(StringComparer.Ordinal).Should().Equal("issues", "push");
        targets[2].AcceptedEvents.Should().BeNull("a blank declaration keeps the accept-all contract");
        targets[2].Accepts("check_run").Should().BeTrue();
    }

    /// <summary>A set assigned in code with the DEFAULT (case-sensitive) comparer is normalized on
    /// init: the target still matches ignoring case, as configuration-read targets do; an empty set
    /// is no declaration.</summary>
    [Fact]
    public void AcceptedEvents_AssignedWithTheDefaultComparer_StillMatchIgnoringCase()
    {
        var target = new WebhookInbox.WebhookTarget("Hosting/PlatformBuilds")
        {
            AcceptedEvents = ImmutableHashSet.Create("pull_request"),
        };

        target.AcceptedEvents!.KeyComparer.Should().BeSameAs(StringComparer.OrdinalIgnoreCase);
        target.Accepts("PULL_REQUEST").Should().BeTrue();
        target.Accepts("check_run").Should().BeFalse();
        new WebhookInbox.WebhookTarget("X") { AcceptedEvents = ImmutableHashSet<string>.Empty }
            .AcceptedEvents.Should().BeNull("an empty set is no declaration");
    }
}
