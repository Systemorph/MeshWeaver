#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.SelfUpdate;
using Memex.Portal.Shared.Settings;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The whole announcement-key round trip (Doc/Architecture/SelfUpdateAnnouncementKey), with no
/// operator and no restart:
/// <list type="number">
/// <item>the CONTROL instance issues a key for <c>fabrikam</c> (a portal-set per-sender key);</item>
/// <item>fabrikam's administrator SAVES the same key in Settings ▸ Control lane;</item>
/// <item>the two fingerprints match;</item>
/// <item><b>Test connection</b> sends a signed, verify-only test, and the control inbox answers
/// "verified as fabrikam" and stores nothing;</item>
/// <item>a mistyped key answers a MISMATCH;</item>
/// <item>no log line carries the key.</item>
/// </list>
/// Both roles share one mesh: the sender signs with <c>Hosting:ControlInbox:Secret</c>, and the
/// inbox verifies with <c>Hosting:PlatformWebhookSecret:fabrikam</c>. The HTTP hop is an in-process
/// handler that answers exactly as <see cref="WebhookInboxEndpoints"/> does.
/// </summary>
public class ControlLaneKeyRoundTripTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string AdminPartition = "Admin";
    private const string PlatformAdmin = "platform-boss";
    private const string Deployment = "fabrikam";
    private const string InboxUrl = "https://control.test" + SelfUpdateHandover.InboxRoute;
    private const string FleetSecret = "the-fleet-wide-secret";

    private static readonly string MasterKey = Convert.ToBase64String(Enumerable.Range(7, 32).Select(i => (byte)i).ToArray());

    private readonly CapturingLoggerProvider logs = new();

    private static TimeSpan Budget => TestTimeouts.Convergence;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(AdminPartition) { Name = "Admin", NodeType = "Markdown" },
                new MeshNode("Hosting") { Name = "Hosting", NodeType = "Markdown" },
                new MeshNode(SelfUpdateHandover.InboxTarget) { Name = "Platform builds", NodeType = "Markdown", Content = new MarkdownContent { Content = "#\n" } },
                AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", AdminPartition))
            .AddWebhookInbox()
            .AddInstanceSecretSlot(SelfUpdateHandover.SecretKey)
            .AddInstanceSecretSlot(SelfUpdateHandover.LocalSecretKey + ":*")
            .ConfigureServices(services =>
            {
                services.AddSingleton<ILoggerProvider>(logs);
                return services.AddSingleton<IConfiguration>(
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Ai:KeyProtection:MasterKey"] = MasterKey,
                        [SelfUpdateHandover.DeploymentKey] = Deployment,
                        [SelfUpdateHandover.UrlKey] = InboxUrl,
                        [SelfUpdateHandover.LocalSecretKey] = FleetSecret,
                    }).Build());
            });

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private InstanceSecretCatalog Catalog => Mesh.ServiceProvider.GetRequiredService<InstanceSecretCatalog>();

    private Task<InstanceSecretStatus> SaveAsAdmin(string key, string value)
    {
        using (Access.SwitchAccessContext(new AccessContext { ObjectId = PlatformAdmin, Name = PlatformAdmin }))
            return InstanceSecrets.Set(Mesh, key, value).FirstAsync().Timeout(Budget)
                .Await(TestContext.Current.CancellationToken);
    }

    private Task Live(string key, string value) =>
        Catalog.Changes.Where(_ => Catalog.Entry(key)?.Current == value)
            .Should().Within(Budget).Emit($"{key} is live without a restart");

    private SelfUpdateHandover Handover() =>
        new(Mesh, http: new HttpClient(new InProcessInbox(Mesh)));

    [Fact]
    public async Task Issue_save_and_test_round_trip_matches_and_a_mistyped_key_reads_as_a_mismatch()
    {
        var issued = SecretFingerprint.Generate();
        var controlKey = $"{SelfUpdateHandover.LocalSecretKey}:{Deployment}";

        // 1. The control instance issues fabrikam's key.
        var controlStatus = await SaveAsAdmin(controlKey, issued);
        // 2. Fabrikam's administrator pastes it and saves.
        var fabrikamStatus = await SaveAsAdmin(SelfUpdateHandover.SecretKey, issued);
        await Live(controlKey, issued);
        await Live(SelfUpdateHandover.SecretKey, issued);

        // 3. The pair: the two fingerprints are the same, and neither is the key.
        Assert.Equal(controlStatus.Secret.Fingerprint, fabrikamStatus.Secret.Fingerprint);
        Assert.DoesNotContain(issued, fabrikamStatus.Secret.Fingerprint!);

        // The self-updater now has a route to the control instance — no restart, no mount.
        Assert.Equal(SelfUpdateHandover.Route.Post, SelfUpdateHandover.RouteFor(Handover().ReadSettings()));

        // 4. Test connection: verified as fabrikam.
        var outcome = await Handover().Test().FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
        Assert.True(outcome.Accepted, outcome.Detail);
        Assert.Equal(Deployment, outcome.Sender);
        Assert.Contains("Match", ControlLaneSettingsTab.TestMarkdown(outcome, (k, a) => k == "ui.controlLaneTestMatch" ? "Match " + a[0] : k, "en"));

        // The test is recorded on BOTH ends: fabrikam's last use, and the control's last verification.
        await Catalog.Changes
            .Where(_ => Catalog.Entry(SelfUpdateHandover.SecretKey)?.Content.LastUseOk == true
                        && Catalog.Entry(controlKey)?.Content.LastUseOk == true)
            .Should().Within(Budget).Emit("a verified test is recorded as each key's last use");

        // 5. A mistyped key: refused, and the verdict says to re-enter it.
        await SaveAsAdmin(SelfUpdateHandover.SecretKey, issued[..^1] + (issued[^1] == '0' ? '1' : '0'));
        await Catalog.Changes.Where(_ => Catalog.Entry(SelfUpdateHandover.SecretKey)?.Current != issued)
            .Should().Within(Budget).Emit("the re-entered key is live");
        var mismatch = await Handover().Test().FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
        Assert.False(mismatch.Accepted);
        Assert.Contains("401", mismatch.Detail);
        // The verdict renders in the viewer's language: the German reads the German catalog entry.
        Assert.Contains("abgelehnt", ControlLaneSettingsTab.TestMarkdown(mismatch, (k, a) => k + " " + string.Join(" ", a), "de"));

        // Nothing was stored by either test: a key test is verify-only.
        var stored = await Access.RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>()
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{SelfUpdateHandover.InboxTarget}/{WebhookInbox.InboxContainer} scope:children")).Take(1))
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
        Assert.Empty(stored.Items);

        // 6. The key never reached a log line.
        Assert.DoesNotContain(logs.Messages, m => m.Contains(issued[..16], StringComparison.Ordinal));
    }

    /// <summary>
    /// The REVERSED flow the fabrikam rollout uses: the deployment GENERATES its key (shown once, with
    /// its fingerprint), Systemorph registers that value on the control instance, the fingerprints
    /// agree, and Test connection answers a match — with nothing shown again afterwards.
    /// </summary>
    [Fact]
    public async Task The_deployment_generates_the_key_and_the_control_instance_registers_it()
    {
        var controlKey = $"{SelfUpdateHandover.LocalSecretKey}:{Deployment}";
        InstanceSecrets.Generated generated;
        using (Access.SwitchAccessContext(new AccessContext { ObjectId = PlatformAdmin, Name = PlatformAdmin }))
            generated = await InstanceSecrets.Generate(Mesh, SelfUpdateHandover.SecretKey).FirstAsync().Timeout(Budget)
                .Await(TestContext.Current.CancellationToken);
        Assert.Equal(SecretSources.Generate, generated.Status.Secret.Source);

        // Before the control instance registers it, the test reads as a mismatch.
        await Live(SelfUpdateHandover.SecretKey, generated.Value);
        var before = await Handover().Test().FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
        Assert.False(before.Accepted);

        // Systemorph pastes what the deployment's administrator sent over the secure channel.
        var registered = await SaveAsAdmin(controlKey, generated.Value);
        Assert.Equal(generated.Status.Secret.Fingerprint, registered.Secret.Fingerprint);
        await Live(controlKey, generated.Value);

        var after = await Handover().Test().FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
        Assert.True(after.Accepted, after.Detail);
        Assert.Equal(Deployment, after.Sender);
        Assert.DoesNotContain(logs.Messages, m => m.Contains(generated.Value[..16], StringComparison.Ordinal));
    }

    [Fact]
    public void The_status_block_shows_the_fingerprint_and_never_a_value()
    {
        var key = SecretFingerprint.Generate();
        var status = InstanceSecrets.StatusOf(SelfUpdateHandover.SecretKey, null, key);
        var markdown = WriteOnlySecretSection.StatusMarkdown(status.Secret, (k, a) => k + " " + string.Join(" | ", a))
            + ControlLaneSettingsTab.DetailMarkdown(status, (k, a) => k + " " + string.Join(" | ", a), "en");
        Assert.Contains(SecretFingerprint.Of(key)!, markdown);
        Assert.DoesNotContain(key, markdown);
        Assert.DoesNotContain(key[..16], markdown);
    }

    [Theory]
    [InlineData(200, """{"status":"verified","signature":"verified","sender":"fabrikam"}""", true, "fabrikam")]
    [InlineData(200, """{"status":"verified","signature":"verified","sender":null}""", true, null)]
    [InlineData(401, "", false, null)]
    // An older control instance that does not know the verify-only header STORES the test instead.
    [InlineData(200, """{"status":"accepted","signature":"verified"}""", false, null)]
    [InlineData(200, """{"status":"verified","signature":"not-required"}""", false, null)]
    [InlineData(500, "", false, null)]
    public void A_test_answer_is_accepted_only_when_verified(int code, string body, bool accepted, string? sender)
    {
        var outcome = SelfUpdateHandover.TestOutcomeOf(InboxUrl, code, body);
        Assert.Equal(accepted, outcome.Accepted);
        Assert.Equal(sender, outcome.Sender);
    }

    /// <summary>The control inbox, in process: delivers into THIS mesh and answers as the endpoint does.</summary>
    private sealed class InProcessInbox(IMessageHub mesh) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.Select(h => new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value))).ToList();
            var targets = new[] { new WebhookInbox.WebhookTarget(SelfUpdateHandover.InboxTarget, SelfUpdateHandover.LocalSecretKey) };
            var result = await WebhookInbox.Deliver(mesh, targets, SelfUpdateHandover.InboxTarget, "application/json", headers, body)
                .FirstAsync().Await(cancellationToken);
            return result.Status switch
            {
                WebhookInbox.DeliveryStatus.Accepted => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(WebhookInboxEndpoints.AcceptedAnswer(result)), Encoding.UTF8, "application/json"),
                },
                WebhookInbox.DeliveryStatus.SignatureInvalid => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            };
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> messages = new();

        public IEnumerable<string> Messages => messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new Capture(messages);

        public void Dispose() { }

        private sealed class Capture(System.Collections.Concurrent.ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                sink.Enqueue(formatter(state, exception));
                if (exception is not null)
                    sink.Enqueue(exception.ToString());
            }
        }
    }
}
