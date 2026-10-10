#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 A webhook delivery is answered as soon as it is DURABLY STORED, not once the create's
/// reply has made its way back (MeshWeaver#6039).
///
/// <para>GitHub recorded a delivery as "no response within 10 s" although the portal had logged
/// <c>Node created at Hosting/PlatformBuilds/_Inbox/…</c> 200 ms after it was sent. The endpoint
/// answered on the <c>CreateNodeResponse</c>. That reply is posted after the post-creation
/// handlers and leaves through the node-operation hub's own action block, so anything holding
/// either one held the HTTP answer for a row that was already committed.</para>
///
/// <para>The repro holds the create's reply after the commit, deterministically: a post-creation
/// handler for <c>WebhookEvent</c> that does not complete until the test releases it in a
/// <c>finally</c>. The sender's own limit is the bound, GitHub's 10 s, set as the client's
/// timeout. The create's verdict deadline is 30 s, so an endpoint that waits for the reply cannot
/// answer inside it.</para>
///
/// <para><b>Negative control.</b> With <c>WebhookInbox</c>'s store going back to
/// <c>mesh.CreateNode(node).Take(1)</c>, <see cref="ADeliveryWhoseReplyIsHeld_IsAnsweredOnTheCommit"/>
/// goes red: the client gives up after 10 s with the handler still held, which is the incident.</para>
/// </summary>
public class WebhookInboxAnswersOnCommitTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Target = "Hosting/HeldInbox";
    private const string Route = "/api/hooks/" + Target;

    /// <summary>GitHub's delivery timeout: a sender that waits longer records no response.</summary>
    private static readonly TimeSpan SenderTimeout = TimeSpan.FromSeconds(10);

    private readonly HeldWebhookHandler handler = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddWebhookInbox()
        .ConfigureServices(services => services.AddSingleton<INodePostCreationHandler>(handler));

    private async Task<WebApplication> StartHost(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{WebhookInbox.TargetsConfigSection}:0"] = Target,
        });
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        var app = builder.Build();
        app.MapWebhookInbox();
        await app.StartAsync(ct);
        return app;
    }

    private async Task SeedTarget(CancellationToken ct)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        // RunAsSystem, never Observable.Using(ImpersonateAsSystem, …) (#1790).
        await access.RunAsSystem(
                () => Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateOrUpdateNode(
                    new MeshNode(Target)
                    {
                        Name = Target,
                        NodeType = "Markdown",
                        Content = new MarkdownContent { Content = "# Inbox target\n" },
                    }))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
    }

    private static HttpClient SenderClient(WebApplication app)
    {
        var client = app.GetTestClient();
        client.Timeout = SenderTimeout;
        return client;
    }

    /// <summary>
    /// THE CHANGE: the row is committed and its reply is held. The sender still gets its 200
    /// inside its own limit, while the handler is provably still running.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ADeliveryWhoseReplyIsHeld_IsAnsweredOnTheCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTarget(ct);
        await using var app = await StartHost(ct);
        using var client = SenderClient(app);

        handler.Holding = true;
        try
        {
            using var response = await client.PostAsync(Route,
                new StringContent("{\"action\":\"edited\"}", Encoding.UTF8, "application/json"), ct);

            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "the delivery was durably stored, which is all the sender is owed");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            json.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            handler.StillHeld.Should().BeTrue(
                "the answer must not have waited for the create's reply: the handler that holds the "
                + "reply had not been released when the sender was answered");
            await handler.EnteredSignal.Should().Within(TestTimeouts.Quick).Emit(
                "the post-creation handler runs only after the commit, so it being entered proves "
                + "the row was written");
        }
        finally
        {
            handler.Release();
        }
    }

    /// <summary>
    /// THE CONTROL: nothing is held, and the answer is the same 200. A route that answered without
    /// storing anything would never enter the handler, which the case above asserts.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ADeliveryWithNothingHeld_IsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTarget(ct);
        await using var app = await StartHost(ct);
        using var client = SenderClient(app);

        handler.Holding = false;
        using var response = await client.PostAsync(Route,
            new StringContent("{\"action\":\"opened\"}", Encoding.UTF8, "application/json"), ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await handler.EnteredSignal.Should().Within(TestTimeouts.Quick).Emit(
            "the delivery was stored, so its post-creation step ran");
    }

    /// <summary>
    /// A post-creation handler for <c>WebhookEvent</c> that, while <see cref="Holding"/>, does not
    /// complete until <see cref="Release"/>: the reply of the create is held after its commit. The
    /// release is an <see cref="AsyncSubject{T}"/> the test completes, never a blocked thread.
    /// </summary>
    private sealed class HeldWebhookHandler : INodePostCreationHandler
    {
        private readonly AsyncSubject<Unit> release = new();
        private readonly AsyncSubject<Unit> enteredSignal = new();
        private int holding;

        public string NodeType => WebhookInbox.NodeType;

        public bool Holding
        {
            get => Volatile.Read(ref holding) == 1;
            set => Interlocked.Exchange(ref holding, value ? 1 : 0);
        }

        /// <summary>Completes when a <c>WebhookEvent</c>'s post-creation step was entered.</summary>
        public IObservable<Unit> EnteredSignal => enteredSignal.AsObservable();

        public bool StillHeld => !release.IsCompleted;

        public void Release()
        {
            release.OnNext(Unit.Default);
            release.OnCompleted();
        }

        public IObservable<Unit> Handle(MeshNode createdNode, string? createdBy)
        {
            enteredSignal.OnNext(Unit.Default);
            enteredSignal.OnCompleted();
            return Holding ? release.AsObservable() : Observable.Return(Unit.Default);
        }
    }
}
