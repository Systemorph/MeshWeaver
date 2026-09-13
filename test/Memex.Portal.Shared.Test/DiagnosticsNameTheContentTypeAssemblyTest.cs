#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;   // IMeshContentTypeRegistry
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>#4158, ask 2 — <c>get_diagnostics</c> names the assembly a NodeType's content type
/// actually resolved to.</b>
///
/// <para>Before this, every identity on that reply was a CLAIM written by a build: <c>mvid</c> is
/// <c>NodeTypeDefinition.LatestAssemblyMvid</c> (the bytes a build produced — and the record's own
/// doc says <c>LatestAssemblyPath</c> "is an ADDRESS, not an identity"), and <c>[ModuleLoad]</c>
/// names the assembly a module LOADED. Neither is the statement the serializer and
/// <c>/schema/&lt;Type&gt;</c> act on. On 2026-09-10 that gap cost a night: a module had the newest
/// generation, the newest <c>written=</c> and types predating two merged PRs, and from the consumer
/// a stale ADOPTED build and a stale registry SHELF read identically.</para>
///
/// <para>Both cases below FAIL on <c>main</c>: the reply carries no <c>contentType</c> member at
/// all, so <c>TryGetProperty</c> is false and the first assertion of each goes red.</para>
///
/// <para><b>The second case is the negative control.</b> Same mesh, same call, a NodeType with no
/// registered content type — it must report <c>unresolved</c>. If the block were a constant of the
/// reply (or derived from the node's record rather than from the live registry) both cases would
/// answer the same way and this one would fail.</para>
/// </summary>
public class DiagnosticsNameTheContentTypeAssemblyTest(ITestOutputHelper testOutput)
    : MonolithMeshTestBase(testOutput)
{
    private const string TypedNodeType = "DiagTyped";
    private const string UntypedNodeType = "DiagUntyped";

    /// <summary>The content type the typed NodeType's diagnostics must name the assembly of.</summary>
    public sealed record DiagPayload(string Value);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(
                new MeshNode(TypedNodeType)
                {
                    Name = TypedNodeType,
                    NodeType = MeshNode.NodeTypePath,
                    Content = new NodeTypeDefinition { DefaultNamespace = "" },
                },
                new MeshNode(UntypedNodeType)
                {
                    Name = UntypedNodeType,
                    NodeType = MeshNode.NodeTypePath,
                    Content = new NodeTypeDefinition { DefaultNamespace = "" },
                });

    private async Task<JsonElement> DiagnosticsFor(string nodeTypePath)
    {
        var json = await new MeshOperations(Mesh).GetDiagnostics(nodeTypePath)
            .Timeout(TestTimeouts.CrossSilo).FirstAsync();
        Output.WriteLine($"{nodeTypePath} → {json}");
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact(Timeout = 120_000)]
    public async Task ARegisteredContentTypeIsNamedByItsOwnAssemblyAndMvid()
    {
        Mesh.ServiceProvider.GetRequiredService<IMeshContentTypeRegistry>()
            .Register(typeof(DiagPayload), TypedNodeType);

        var reply = await DiagnosticsFor(TypedNodeType);

        Assert.True(reply.TryGetProperty("contentType", out var block),
            "the per-NodeType diagnostics must carry the assembly the content type resolved to — "
            + "every other identity on this reply is a claim some build wrote (#4158 ask 2). "
            + $"Got: {reply}");
        // 🚨 The LITERAL, not the constant: this is a control over the WIRE shape a consumer
        // reads, and asserting against the constant would let a rename change the
        // expectation silently. It also lets this file compile — and fail — on `main`.
        Assert.Equal("resolved", block.GetProperty("status").GetString());
        Assert.Equal(typeof(DiagPayload).FullName, block.GetProperty("typeName").GetString());
        // The LOADED assembly's own path and module version id — properties of the bytes in this
        // process, which is the only coordinate here that cannot be stale.
        Assert.Equal(typeof(DiagPayload).Assembly.Location,
            block.GetProperty("assembly").GetString());
        Assert.Equal(
            typeof(DiagPayload).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..8],
            block.GetProperty("mvid").GetString());
        Assert.False(block.GetProperty("collectible").GetBoolean());
    }

    [Fact(Timeout = 120_000)]
    public async Task ANodeTypeWithNoRegisteredContentTypeSaysSo_NotNothing()
    {
        var reply = await DiagnosticsFor(UntypedNodeType);

        Assert.True(reply.TryGetProperty("contentType", out var block),
            $"the block must be present whatever the answer is. Got: {reply}");
        // 🚨 The negative control. If the block were a constant of the reply, or read off the
        // node's record instead of the live registry, this would say "resolved" like the case
        // above — and the case above would then be measuring nothing.
        Assert.Equal("unresolved", block.GetProperty("status").GetString());
        Assert.Null(block.GetProperty("typeName").GetString());
        Assert.Null(block.GetProperty("assembly").GetString());
        // The consequence, spelled out: this is the state in which the node's views render blank.
        Assert.Contains("render empty", block.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
    }
}
