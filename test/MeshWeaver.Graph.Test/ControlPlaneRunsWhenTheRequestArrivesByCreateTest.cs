using System;
using System.Reactive.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#3153 — a control-plane request filed by a CREATE has to actually run.</b>
///
/// <para>A <c>RequestedXxx</c> field is a request addressed to a watcher the owning per-node hub
/// installs in its <c>WithInitialization</c> (<c>Doc/Architecture/RequestViaStreamUpdate</c>), and
/// that watcher runs <b>on activation</b>. A create only writes the row — so the request sat at
/// <c>Requested</c> with no error, no log line and no failed state, until something unrelated
/// happened to open the node. Measured on memex.meshweaver.cloud: a <c>Store/Subscription</c>
/// created via MCP sat untouched for 4.5 h and completed 20 s after the first read.</para>
///
/// <para><b>Why this never showed up from the UI, and why the existing suites miss it.</b> Creating
/// the same node from a page leaves the page's own stream handle open, which activates the owner as
/// a side effect. Every control-plane test in the fleet does the equivalent — it subscribes to the
/// node to await a terminal state — and <b>the subscription is what makes the watcher run</b>. Such
/// a test passes with the defect fully present.</para>
///
/// <para>🚨 So this test never opens the node's stream. It files the request with
/// <see cref="IMeshService.CreateNode"/> and then polls DURABLE STORAGE
/// (<see cref="IStorageAdapter"/>) for the terminal state — the one observation channel that does
/// not itself activate the owner. Reading through <c>GetMeshNodeStream</c> here would be the
/// assertion causing the very thing it claims to observe.</para>
/// </summary>
public class ControlPlaneRunsWhenTheRequestArrivesByCreateTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string ControlPlaneNodeType = "Test/ControlPlane";
    private const string Activate = "Activate";
    private const string Done = "Done";

    /// <summary>Minimal control-plane content: the request field and the state it drives.</summary>
    public record ControlPlaneContent
    {
        [JsonPropertyName("requestedAction")]
        public string? RequestedAction { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(CreateControlPlaneNodeType())
            .ConfigureHub(config =>
                config.WithType<ControlPlaneContent>(nameof(ControlPlaneContent)));

    /// <summary>
    /// The node type under test, wired exactly the way the production control planes are: the
    /// watcher is installed by the per-node hub's own initialization, so it runs if and only if
    /// that hub is activated.
    /// </summary>
    private static MeshNode CreateControlPlaneNodeType() => new(ControlPlaneNodeType)
    {
        Name = "Control Plane Probe",
        HubConfiguration = config => config
            .AddMeshDataSource(s => s.WithContentType<ControlPlaneContent>())
            .WithInitialization(hub =>
            {
                var path = hub.Address.ToString();
                hub.GetMeshNodeStream(path)
                    .Where(node => node?.ContentAs<ControlPlaneContent>(hub.JsonSerializerOptions)
                        is { RequestedAction: Activate, Status: null })
                    .Take(1)
                    .SelectMany(_ => hub.GetMeshNodeStream(path).Update(node =>
                    {
                        var content = node.ContentAs<ControlPlaneContent>(hub.JsonSerializerOptions)
                                      ?? new ControlPlaneContent();
                        return node with
                        {
                            Content = content with { RequestedAction = null, Status = Done },
                        };
                    }))
                    // Cold — subscribe or the write never happens — with an explicit error arm.
                    .Subscribe(_ => { }, _ => { });
            }),
    };

    // 180_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a constant, so
    // the outer cap cannot be computed. The WAIT inside uses TestTimeouts.Convergence — that is the
    // bound the ratchet guard is about; this one only stops a wedge from running forever.
    [Fact(Timeout = 180_000)]
    public async Task ARequestFiledByCreate_RunsWithoutAnybodyOpeningTheNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var path = $"{TestPartition}/control-plane-on-create";

        // File the request the way every non-UI writer does: MCP, a script, a billing webhook.
        await meshService.CreateNode(
                new MeshNode("control-plane-on-create", TestPartition)
                {
                    NodeType = ControlPlaneNodeType,
                    Name = "Control plane on create",
                    State = MeshNodeState.Active,
                    Content = new ControlPlaneContent { RequestedAction = Activate },
                })
            .Should().Within(TestTimeouts.Convergence).Emit("the create itself must succeed");

        // 🚨 Nothing here opens the node's stream — storage is the only channel that does not
        // activate the owner, and activating it is the whole subject.
        var settled = await Observable.Interval(TimeSpan.FromMilliseconds(200)).StartWith(0L)
            .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
            .Where(node => node?.ContentAs<ControlPlaneContent>(Mesh.JsonSerializerOptions)
                is { Status: Done })
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

        settled!.ContentAs<ControlPlaneContent>(Mesh.JsonSerializerOptions)!.Status
            .Should().Be(Done,
                "a control-plane request is addressed to a watcher that only runs when the owning "
                + "hub is activated; a create that files one and activates nothing leaves it at "
                + "Requested forever, with no error and nothing that would ever surface it");
    }
}
