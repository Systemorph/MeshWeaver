#pragma warning disable CS1591

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pure tests for <see cref="NavigationContextProjection"/> — the projection that turns the page
/// the user is viewing into the <see cref="AgentContext"/> shipped to the agent.
///
/// 🚨 These pin the rules the chat regressed on: the context ALWAYS resolves to the MAIN NODE
/// (never a satellite), and it carries the full navigation reference — owner address + layout
/// area + optional query parameters as key/value pairs. The agent gets a reference; it loads
/// content via its Get tool (we never inline content). No mesh required.
/// </summary>
public class NavigationContextProjectionTest
{
    private static NavigationContext Nav(
        string @namespace,
        MeshNode? node = null,
        string? area = null,
        string? id = null,
        IReadOnlyDictionary<string, string>? args = null)
        => new()
        {
            Path = @namespace,
            Resolution = new AddressResolution(@namespace, null, node),
            Node = node,
            Area = area,
            Id = id,
            Args = args is null
                ? ImmutableDictionary<string, string>.Empty
                : args.ToImmutableDictionary(),
        };

    [Fact]
    public void SatelliteNode_ResolvesToOwnerMainNode_NotTheSatellite()
    {
        // Viewing a thread (a satellite) → the chat context must be the thread's OWNER, never
        // the thread path itself. This is the exact "nest a thread under a thread" bug.
        var node = MeshNode.FromPath("rbuergi/_Thread/hi-3cb8") with { MainNode = "rbuergi" };
        var ctx = Nav("rbuergi/_Thread/hi-3cb8", node);

        var agentContext = NavigationContextProjection.ToAgentContext(ctx);

        Assert.Equal("rbuergi", agentContext.Address!.ToString());
        Assert.Equal("rbuergi", agentContext.Context);
    }

    [Fact]
    public void Satellite_NodeNotYetLoaded_StripsSatelliteSegmentsFromNamespace()
    {
        // The node hasn't hydrated (Node == null) — fall back to stripping the satellite
        // segment off the resolved namespace so we still never ship the satellite path.
        var ctx = Nav("rbuergi/_Thread/hi-3cb8", node: null);

        var agentContext = NavigationContextProjection.ToAgentContext(ctx);

        Assert.Equal("rbuergi", agentContext.Address!.ToString());
    }

    [Fact]
    public void ThreadAboutAnotherNode_UsesMainNode_NotTheStringStrippedPath()
    {
        // A thread stored under the user's partition but ABOUT another node points its MainNode
        // there. The authoritative MainNode must win over the path's string-strip ("rbuergi").
        var node = MeshNode.FromPath("rbuergi/_Thread/hi-7a2c") with { MainNode = "Doc/Architecture/Page" };
        var ctx = Nav("rbuergi/_Thread/hi-7a2c", node);

        var agentContext = NavigationContextProjection.ToAgentContext(ctx);

        Assert.Equal("Doc/Architecture/Page", agentContext.Address!.ToString());
    }

    [Fact]
    public void RegularNode_ResolvesToItself()
    {
        // A non-satellite node (MainNode == Path) is its own context.
        var node = MeshNode.FromPath("Doc/Architecture/Page") with { MainNode = "Doc/Architecture/Page" };
        var ctx = Nav("Doc/Architecture/Page", node);

        var agentContext = NavigationContextProjection.ToAgentContext(ctx);

        Assert.Equal("Doc/Architecture/Page", agentContext.Address!.ToString());
    }

    [Fact]
    public void CapturesLayoutAreaAndId()
    {
        var node = MeshNode.FromPath("Doc/Page") with { MainNode = "Doc/Page" };
        var ctx = Nav("Doc/Page", node, area: "VersionDiff", id: "v12");

        var agentContext = NavigationContextProjection.ToAgentContext(ctx);

        Assert.NotNull(agentContext.LayoutArea);
        Assert.Equal("VersionDiff", agentContext.LayoutArea!.Area);
        Assert.Equal("v12", agentContext.LayoutArea.Id);
    }

    [Fact]
    public void CapturesOptionalParametersAsKeyValuePairs()
    {
        var node = MeshNode.FromPath("Doc/Page") with { MainNode = "Doc/Page" };
        var ctx = Nav("Doc/Page", node, area: "VersionDiff",
            args: new Dictionary<string, string> { ["from"] = "5", ["to"] = "8" });

        var agentContext = NavigationContextProjection.ToAgentContext(ctx);

        Assert.NotNull(agentContext.Parameters);
        Assert.Equal("5", agentContext.Parameters!["from"]);
        Assert.Equal("8", agentContext.Parameters["to"]);
    }

    [Fact]
    public void NoArea_NoParameters_LeavesThemNull()
    {
        var node = MeshNode.FromPath("Doc/Page") with { MainNode = "Doc/Page" };
        var ctx = Nav("Doc/Page", node);

        var agentContext = NavigationContextProjection.ToAgentContext(ctx);

        Assert.Null(agentContext.LayoutArea);
        Assert.Null(agentContext.Parameters);
    }

    [Fact]
    public void NullContext_YieldsEmptyContext()
    {
        var agentContext = NavigationContextProjection.ToAgentContext(null);

        Assert.Null(agentContext.Address);
        Assert.Null(agentContext.LayoutArea);
        Assert.Null(agentContext.Parameters);
    }

    [Fact]
    public void ToReference_CapturesAreaAndParameters()
    {
        var ctx = Nav("Doc/Page", area: "VersionDiff", id: "v12",
            args: new Dictionary<string, string> { ["from"] = "5", ["to"] = "8" });

        var reference = NavigationContextProjection.ToReference(ctx);

        Assert.NotNull(reference);
        Assert.Equal("VersionDiff", reference!.Area);
        Assert.Equal("v12", reference.AreaId);
        Assert.Equal("5", reference.Parameters!["from"]);
    }

    [Fact]
    public void ToReference_NoAreaNoParameters_IsNull()
    {
        // Nothing extra to carry beyond the main-node address (which rides as the context path).
        Assert.Null(NavigationContextProjection.ToReference(Nav("Doc/Page")));
        Assert.Null(NavigationContextProjection.ToReference(null));
    }

    [Fact]
    public void NavigationReference_RoundTripsThroughJson()
    {
        // The reference is JSON-serialized into the carry field and deserialized at the round.
        // Pin the wire round-trip so a serialization change can't silently drop area/params.
        var reference = NavigationContextProjection.ToReference(
            Nav("Doc/Page", area: "VersionDiff", id: "v12",
                args: new Dictionary<string, string> { ["from"] = "5", ["to"] = "8" }));

        var json = JsonSerializer.Serialize(reference, new JsonSerializerOptions());
        var back = JsonSerializer.Deserialize<NavigationReference>(json, new JsonSerializerOptions());

        Assert.NotNull(back);
        Assert.Equal("VersionDiff", back!.Area);
        Assert.Equal("v12", back.AreaId);
        Assert.Equal("8", back.Parameters!["to"]);
    }

    // ── ResolveContext: the composer's chat context = the viewed page's MAIN NODE ──────────────
    // The composer must pin the OWNER of whatever the user is looking at — never a satellite, never
    // a reserved route, never the composer node's own home partition.

    [Fact]
    public void ResolveContext_RegularNode_IsTheNodeItself()
    {
        var node = MeshNode.FromPath("Doc/Architecture/Page") with { MainNode = "Doc/Architecture/Page" };
        Assert.Equal("Doc/Architecture/Page",
            NavigationContextProjection.ResolveContext(Nav("Doc/Architecture/Page", node)));
    }

    [Fact]
    public void ResolveContext_DeepNode_KeepsTheFullPath_NotPartitionRoot()
    {
        // Must NOT strip down to the partition root — the context is the actual node viewed.
        var node = MeshNode.FromPath("rbuergi/Projects/Alpha") with { MainNode = "rbuergi/Projects/Alpha" };
        Assert.Equal("rbuergi/Projects/Alpha",
            NavigationContextProjection.ResolveContext(Nav("rbuergi/Projects/Alpha", node)));
    }

    [Fact]
    public void ResolveContext_Satellite_ResolvesToOwner()
    {
        var node = MeshNode.FromPath("rbuergi/_Thread/hi-3cb8") with { MainNode = "rbuergi" };
        Assert.Equal("rbuergi",
            NavigationContextProjection.ResolveContext(Nav("rbuergi/_Thread/hi-3cb8", node)));
    }

    [Fact]
    public void ResolveContext_SatelliteNodeNotLoaded_StripsToOwner()
    {
        Assert.Equal("rbuergi",
            NavigationContextProjection.ResolveContext(Nav("rbuergi/_Thread/hi-3cb8", node: null)));
    }

    [Fact]
    public void ResolveContext_ReservedRoutePartition_IsEmpty()
    {
        Assert.Equal(string.Empty, NavigationContextProjection.ResolveContext(Nav("login")));
        Assert.Equal(string.Empty, NavigationContextProjection.ResolveContext(Nav("welcome")));
    }

    [Fact]
    public void ResolveContext_ChatRoute_IsEmpty()
    {
        // The composer's own route is not a context node.
        Assert.Equal(string.Empty, NavigationContextProjection.ResolveContext(Nav("chat")));
    }

    [Fact]
    public void ResolveContext_NullContext_IsEmpty()
    {
        Assert.Equal(string.Empty, NavigationContextProjection.ResolveContext(null));
    }

    [Fact]
    public void ServerSideAssembly_MergesMainNodePathReferenceAndNode()
    {
        // ThreadExecution path: main node resolved (the context path), the carried reference
        // (area + params), and the loaded node identity combine into the shipped context.
        var reference = new NavigationReference
        {
            Area = "VersionDiff",
            AreaId = "v12",
            Parameters = new Dictionary<string, string> { ["from"] = "5" }.ToImmutableDictionary(),
        };
        var node = MeshNode.FromPath("Doc/Page") with { NodeType = "Markdown", Name = "Page" };

        var agentContext = NavigationContextProjection.ToAgentContext("Doc/Page", reference, node);

        Assert.Equal("Doc/Page", agentContext.Address!.ToString());
        Assert.Equal("VersionDiff", agentContext.LayoutArea!.Area);
        Assert.Equal("v12", agentContext.LayoutArea.Id);
        Assert.Equal("5", agentContext.Parameters!["from"]);
        Assert.Same(node, agentContext.Node);
    }

    [Fact]
    public void SerializesWithStandardOptions_CarryingAddressAreaAndParameters()
    {
        // The context object is JSON-serialized with the mesh's normal options before it is
        // shipped. Pin that the serialized payload carries the owner address, the area, and the
        // parameter KVPs — so a future change that drops one is caught here.
        var node = MeshNode.FromPath("Doc/Page") with { MainNode = "Doc/Page", NodeType = "Markdown", Name = "Page" };
        var ctx = Nav("Doc/Page", node, area: "VersionDiff",
            args: new Dictionary<string, string> { ["from"] = "5", ["to"] = "8" });

        var agentContext = NavigationContextProjection.ToAgentContext(ctx);
        var json = JsonSerializer.Serialize(agentContext, new JsonSerializerOptions());

        Assert.Contains("Doc/Page", json);
        Assert.Contains("VersionDiff", json);
        Assert.Contains("from", json);
        Assert.Contains("\"5\"", json);
    }
}
