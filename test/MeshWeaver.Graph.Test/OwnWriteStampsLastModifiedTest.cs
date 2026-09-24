using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>The probe type's content — a trigger and the state its own hub advances.</summary>
public record OwnWriteProbeContent
{
    /// <summary><c>"Go"</c> asks the node's own hub to advance <see cref="State"/>.</summary>
    public string RequestedAction { get; init; } = "";

    /// <summary>Written ONLY by the node's own hub.</summary>
    public string State { get; init; } = "Proposed";
}

/// <summary>
/// 🚨 An OWN-hub write stamps <see cref="MeshNode.LastModified"/> exactly as a cross-hub write
/// does (Systemorph/MeshWeaver.Plugins#2229, defect D).
///
/// <para>The own-stream path (<c>MeshNodeStreamHandle.UpdateOwn</c>) minted a new version and left
/// <c>LastModified</c> where the last CROSS-HUB write had put it. So a node whose own hub was busy —
/// a thread hub claiming, rolling back and re-claiming a round — read as untouched since the last
/// outside write, and ThreadSupervisor's quiet gauge (<c>LastModified</c>) reported "no change to the
/// node since T" for a hub that was writing at hub speed. The same stale stamp reaches
/// <c>sort:LastModified-desc</c> and the node page's "Updated" line.</para>
///
/// <para>Shape: a cross-hub patch sets the trigger (stamped T1 by the cross-hub path); the node's
/// own watcher answers it with an own-stream write. After that write the node's version has
/// advanced AND its <c>LastModified</c> is strictly later than T1.</para>
/// </summary>
public class OwnWriteStampsLastModifiedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ProbeType = "OwnWriteProbe";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        builder.AddMeshNodes(new MeshNode(ProbeType)
        {
            Name = "Own-write probe",
            HubConfiguration = config => config
                .WithContentType<OwnWriteProbeContent>()
                .WithInitialization(hub =>
                {
                    var workspace = hub.GetWorkspace();
                    hub.RegisterForDisposal(workspace.GetMeshNodeStream()
                        .Where(node => node is not null)
                        .Select(node => Advance(hub, workspace, node!))
                        .Concat()
                        .Subscribe(_ => { }, _ => { }));
                }),
        });
        builder.ConfigureHub(config => config.WithType<OwnWriteProbeContent>(nameof(OwnWriteProbeContent)));
        return base.ConfigureMesh(builder);
    }

    /// <summary>The own hub consumes the trigger — an OWN-stream write (no path: this hub's node).</summary>
    private static IObservable<Unit> Advance(IMessageHub hub, IWorkspace workspace, MeshNode node)
    {
        var content = node.ContentAs<OwnWriteProbeContent>(hub.JsonSerializerOptions);
        if (content is null || content.RequestedAction != "Go")
            return Observable.Empty<Unit>();
        return workspace.GetMeshNodeStream()
            .Update(live =>
            {
                var c = live.ContentAs<OwnWriteProbeContent>(hub.JsonSerializerOptions);
                return c is null
                    ? live
                    : live with { Content = c with { State = "Done", RequestedAction = "" } };
            })
            .Select(_ => Unit.Default);
    }

    [Fact(Timeout = 120000)]
    public async Task AnOwnHubWrite_AdvancesLastModified()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = "OwnWrite" + Guid.NewGuid().ToString("N")[..8];
        var path = $"{TestPartition}/{id}";

        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
            {
                Name = id,
                NodeType = ProbeType,
                Content = new OwnWriteProbeContent(),
            }).Take(1)
            .Should().Within(60.Seconds()).Emit("the probe node is created", cancellationToken: ct);

        // The cross-hub trigger — stamped by the cross-hub path.
        var triggered = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(live =>
            {
                var c = live.ContentAs<OwnWriteProbeContent>(Mesh.JsonSerializerOptions);
                return c is null ? live : live with { Content = c with { RequestedAction = "Go" } };
            })
            .Should().Within(60.Seconds()).Emit("the cross-hub trigger lands", cancellationToken: ct);

        // The own hub's answer — the state only the node's own hub writes.
        var done = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.ContentAs<OwnWriteProbeContent>(Mesh.JsonSerializerOptions) is { State: "Done" })
            .Take(1)
            .Should().Within(60.Seconds()).Emit("the node's own hub consumes the trigger", cancellationToken: ct);

        done!.Version.Should().BeGreaterThan(triggered!.Version,
            "the own-hub write minted a version — the write landed");
        done.LastModified.Should().BeAfter(triggered.LastModified,
            "an own-hub write is a change to the node like any other, so it stamps LastModified; "
            + "leaving it at the last cross-hub stamp makes a busy node read as quiet (Plugins#2229 D)");
    }
}
