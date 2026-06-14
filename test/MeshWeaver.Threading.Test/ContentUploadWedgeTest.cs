#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Regression for the 2026-06-14 atioz upload wedge. <see cref="MeshOperations.Upload"/> asks the
/// owning node hub for its collection config via <c>hub.Observe(GetDataRequest(ContentCollectionReference))</c>.
/// That request is answered ONLY by <c>HandleCollectionConfigRequest</c>, which is registered ONLY by
/// <c>AddContentCollections()</c>. A target node hub WITHOUT it never answers — and the <c>Take(1)</c> had
/// no <c>.Timeout</c>, so the upload hung forever. Because the MCP/REST boundary does
/// <c>ops.Upload(...).FirstAsync().ToTask()</c>, that wedges the calling request. (The file I/O itself was
/// already correctly pooled through <c>IIoPool</c> — the defect was the un-timed inter-hub round-trip, the
/// only <c>Observe</c> in MeshOperations without a timeout.) The fix bounds the lookup so it surfaces a
/// clean error. This pins it: the upload completes with an Error within a bound — never hangs.
/// </summary>
public class ContentUploadWedgeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // AddAI only — node hubs here have NO content collections, so the target hub never registers
    // HandleCollectionConfigRequest and never answers the ContentCollectionReference lookup.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI();

    [Fact(Timeout = 60000)]
    public void Upload_ToNodeWithoutAMappedCollection_ErrorsBounded_DoesNotWedge()
    {
        var nodePath = $"NoColl{Guid.NewGuid():N}"[..16];
        // Top-level node → seed under System (partition guard).
        SeedTopLevel(new MeshNode(nodePath) { Name = "No collections", NodeType = "Markdown" });

        var bytes = new byte[] { 1, 2, 3, 4 };

        // Contract: an upload MUST complete with a bounded result — a clean "Error: …" or success,
        // never an unbounded hang. Here the target answers the config lookup with no matching
        // collection (fast "not found"); the .Timeout added to that Observe is the production defense
        // for the harder case the unit harness can't stage deterministically — a busy/wedged owning
        // hub that never answers at all, where the un-timed Take(1) used to hang the upload forever
        // (and, through ops.Upload(...).FirstAsync().ToTask(), the calling MCP/REST request).
        var result = new MeshOperations(GetClient())
            .Upload($"{nodePath}/content/logo.png", bytes)
            .Should().Within(45.Seconds()).Emit();

        result.Should().StartWith("Error:",
            "an upload to a node with no matching editable collection must error, never hang");
    }
}
