using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the NODE-TYPE CONTENT OWNERSHIP split: the repo owns a NodeType node's AUTHORED definition
/// (configuration, sources, description, …); the mesh owns its OPERATIONAL compile state (status,
/// timestamps, assembly pointers, source-version maps, release triggers). The split is enforced at
/// three seams — export strips the operational members, import preserves the LIVE node's values,
/// and the change-detection token ignores them entirely.
///
/// <para><b>What went wrong without it</b> (memex, 2026-08-02): every GitSync export embedded the
/// compile bookkeeping into the repo's <c>index.json</c>, and every later import stamped that
/// stale copy back over the live node — a STALE GREEN claiming a weeks-old assembly was current
/// (the class that parks a type on the next cold cache), which made every plugin-source deploy a
/// force-sync + manual-recompile ritual. And because the token hashed the bookkeeping, a repo
/// diff consisting of nothing but stale bookkeeping re-imported (and recompiled) types whose
/// authored content never changed.</para>
/// </summary>
public class NodeTypeOperationalContentTest
{
    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static MeshNode TypeNode(object? content) =>
        new("Plugin", "Store") { NodeType = MeshNode.NodeTypePath, Name = "Plugin", Content = content };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ── The member list is pinned to the record ─────────────────────────────────────────────

    [Fact]
    public void EveryOperationalMember_IsARealNodeTypeDefinitionProperty()
    {
        var properties = typeof(NodeTypeDefinition).GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var member in NodeTypeOperationalContent.MemberNames)
            Assert.True(properties.Contains(member),
                $"'{member}' is listed as operational but is not a NodeTypeDefinition property — "
                + "the list drifted from the record.");
    }

    [Fact]
    public void AuthoredMembers_AreNeverListedAsOperational()
    {
        // The authored surface — what the repo owns. If one of these ever lands in MemberNames,
        // imports would stop honouring the repo for it (silently).
        string[] authored =
        [
            nameof(NodeTypeDefinition.Description), nameof(NodeTypeDefinition.Configuration),
            nameof(NodeTypeDefinition.HubConfiguration), nameof(NodeTypeDefinition.Sources),
            nameof(NodeTypeDefinition.Tests), nameof(NodeTypeDefinition.IncludeGlobalTypes),
            nameof(NodeTypeDefinition.Dependencies), nameof(NodeTypeDefinition.DefaultNamespace),
            nameof(NodeTypeDefinition.RestrictedToNamespaces), nameof(NodeTypeDefinition.CreatableTypes),
        ];
        foreach (var member in authored)
            Assert.False(NodeTypeOperationalContent.MemberNames.Contains(member),
                $"'{member}' is authored content and must never be masked as operational.");
    }

    // ── Strip (the export / token shape) ────────────────────────────────────────────────────

    [Fact]
    public void Strip_RemovesOperationalMembers_KeepsAuthoredOnes()
    {
        var node = TypeNode(Parse(
            """
            {"$type":"NodeTypeDefinition","description":"d","configuration":"config => config",
             "sources":["namespace:Source scope:subtree"],"includeGlobalTypes":true,
             "compilationStatus":"Ok","lastCompiledVersion":202,
             "latestAssemblyPath":"Store_Plugin/v202.dll","compiledSources":{"a":1}}
            """));
        var stripped = NodeTypeOperationalContent.StripOperational(node, CamelCase);
        var content = Assert.IsType<JsonObject>(stripped.Content);
        Assert.False(content.ContainsKey("compilationStatus"));
        Assert.False(content.ContainsKey("lastCompiledVersion"));
        Assert.False(content.ContainsKey("latestAssemblyPath"));
        Assert.False(content.ContainsKey("compiledSources"));
        Assert.Equal("config => config", (string?)content["configuration"]);
        Assert.Equal("d", (string?)content["description"]);
        Assert.True((bool?)content["includeGlobalTypes"]);
    }

    [Fact]
    public void Strip_HandlesTypedContent_WhateverTheNamingPolicy()
    {
        // Typed content serialized WITHOUT a camelCase policy emits PascalCase members — the
        // match is case-insensitive so both casings denote the same member.
        var node = TypeNode(new NodeTypeDefinition
        {
            Configuration = "config => config",
            CompilationStatus = CompilationStatus.Ok,
            LatestAssemblyPath = "Store_Plugin/v202.dll",
        });
        var stripped = NodeTypeOperationalContent.StripOperational(node, new JsonSerializerOptions());
        var content = Assert.IsType<JsonObject>(stripped.Content);
        Assert.DoesNotContain(content, member =>
            NodeTypeOperationalContent.MemberNames.Contains(member.Key));
        Assert.Contains(content, member =>
            string.Equals(member.Key, "Configuration", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Strip_LeavesOtherNodesAndCleanContentAlone()
    {
        var markdown = new MeshNode("Guide", "Chess") { NodeType = "Markdown", Content = Parse(
            """{"$type":"MarkdownContent","content":"# hi","compilationStatus":"not-a-real-member"}""") };
        Assert.Same(markdown, NodeTypeOperationalContent.StripOperational(markdown, CamelCase));

        var clean = TypeNode(Parse("""{"$type":"NodeTypeDefinition","configuration":"c"}"""));
        Assert.Same(clean, NodeTypeOperationalContent.StripOperational(clean, CamelCase));
    }

    // ── Preserve (the import ownership rule) ────────────────────────────────────────────────

    [Fact]
    public void Preserve_LiveOperationalStateWins_AuthoredComesFromTheRepo()
    {
        // The repo file embeds a STALE verdict (v202, weeks old); the live node has the current
        // one (v1082). The import must take the repo's authored change and keep the live state.
        var incoming = TypeNode(Parse(
            """
            {"$type":"NodeTypeDefinition","configuration":"config => NEW",
             "compilationStatus":"Ok","lastCompiledVersion":202,"latestAssemblyPath":"v202.dll"}
            """));
        var live = TypeNode(Parse(
            """
            {"$type":"NodeTypeDefinition","configuration":"config => OLD",
             "compilationStatus":"Ok","lastCompiledVersion":1082,"latestAssemblyPath":"v1082.dll",
             "currentSourceVersions":{"Store/Plugin/Source/PluginGate":639209062054682760}}
            """));
        var merged = NodeTypeOperationalContent.PreserveLiveOperational(incoming, live, CamelCase);
        var content = Assert.IsType<JsonObject>(merged.Content);
        Assert.Equal("config => NEW", (string?)content["configuration"]);
        Assert.Equal(1082, (long?)content["lastCompiledVersion"]);
        Assert.Equal("v1082.dll", (string?)content["latestAssemblyPath"]);
        Assert.True(content.ContainsKey("currentSourceVersions"));
    }

    [Fact]
    public void Preserve_DropsStaleMembers_TheLiveNodeDoesNotCarry()
    {
        // A live node that never compiled must not inherit the file's stale verdict.
        var incoming = TypeNode(Parse(
            """{"$type":"NodeTypeDefinition","configuration":"c","compilationStatus":"Ok","latestAssemblyPath":"v202.dll"}"""));
        var live = TypeNode(Parse("""{"$type":"NodeTypeDefinition","configuration":"c-old"}"""));
        var merged = NodeTypeOperationalContent.PreserveLiveOperational(incoming, live, CamelCase);
        var content = Assert.IsType<JsonObject>(merged.Content);
        Assert.False(content.ContainsKey("compilationStatus"));
        Assert.False(content.ContainsKey("latestAssemblyPath"));
        Assert.Equal("c", (string?)content["configuration"]);
    }

    [Fact]
    public void Preserve_IsAnIdentityWhenNothingWouldChange()
    {
        // Same operational members, same values → the SAME instance comes back, so an
        // authored-identical import stays a structural no-op upsert (no churn, no version bump).
        var json = """{"$type":"NodeTypeDefinition","configuration":"c","compilationStatus":"Ok"}""";
        var incoming = TypeNode(Parse(json));
        var live = TypeNode(Parse(json));
        Assert.Same(incoming, NodeTypeOperationalContent.PreserveLiveOperational(incoming, live, CamelCase));

        // No live node (a create) and non-NodeType nodes pass through untouched.
        Assert.Same(incoming, NodeTypeOperationalContent.PreserveLiveOperational(incoming, null, CamelCase));
    }

    // ── The change-detection token ignores the operational members ──────────────────────────

    [Fact]
    public void NodeToken_IgnoresOperationalChurn_SeesAuthoredChanges()
    {
        var clean = TypeNode(Parse("""{"$type":"NodeTypeDefinition","configuration":"c","sources":["s"]}"""));
        var churned = TypeNode(Parse(
            """
            {"$type":"NodeTypeDefinition","configuration":"c","sources":["s"],
             "compilationStatus":"Ok","lastCompileSucceededAt":"2026-07-19T13:07:45Z",
             "lastCompiledVersion":202,"compiledSources":{"a":1}}
            """));
        var authored = TypeNode(Parse("""{"$type":"NodeTypeDefinition","configuration":"CHANGED","sources":["s"]}"""));

        Assert.Equal(
            PartitionSourceFingerprint.ComputeNodeToken(clean, CamelCase),
            PartitionSourceFingerprint.ComputeNodeToken(churned, CamelCase));
        Assert.NotEqual(
            PartitionSourceFingerprint.ComputeNodeToken(clean, CamelCase),
            PartitionSourceFingerprint.ComputeNodeToken(authored, CamelCase));
    }

    [Fact]
    public void CodeNodes_StillCompareByTheirFullText()
    {
        // "Compare all code": a Code node's token is its content — a one-character source edit
        // must change it (this is what re-imports a type whose ONLY change is in Source/*.cs).
        MeshNode Code(string text) => new("CouponRedemption", "Store/Plugin/Source")
        {
            NodeType = "Code",
            Content = Parse($$"""{"$type":"CodeConfiguration","code":{{JsonSerializer.Serialize(text)}},"language":"csharp"}"""),
        };
        Assert.NotEqual(
            PartitionSourceFingerprint.ComputeNodeToken(Code("class A { }"), CamelCase),
            PartitionSourceFingerprint.ComputeNodeToken(Code("class A { int x; }"), CamelCase));
        Assert.Equal(
            PartitionSourceFingerprint.ComputeNodeToken(Code("class A { }"), CamelCase),
            PartitionSourceFingerprint.ComputeNodeToken(Code("class A { }"), CamelCase));
    }

    [Fact]
    public void PartitionFingerprint_IsStableAcrossOperationalChurn()
    {
        var content = Parse("""{"$type":"NodeTypeDefinition","configuration":"c"}""");
        var churned = Parse(
            """{"$type":"NodeTypeDefinition","configuration":"c","compilationStatus":"Ok","lastCompiledVersion":7}""");
        var fresh = PartitionSourceFingerprint.Compute(
            [TypeNode(content)], versioned: false, CamelCase);
        var afterChurn = PartitionSourceFingerprint.Compute(
            [TypeNode(churned)], versioned: false, CamelCase);
        Assert.Equal(fresh, afterChurn);
    }
}
