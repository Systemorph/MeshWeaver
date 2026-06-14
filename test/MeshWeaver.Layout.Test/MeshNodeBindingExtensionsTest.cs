using System;
using System.Collections.Generic;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Regression for the "Settings in AgenticPension throws 'Q' is an invalid start of a value" crash.
///
/// <para>A node-bound DataContext (<c>/$meshNode/{base64url(path)}/{c|n}</c>) must resolve its field
/// against the <see cref="MeshNode"/>, NOT the layout-area <c>/data</c> store. The Monaco editor views
/// used to read their <c>Value</c> through <c>Stream.DataBind</c>, which feeds the Base64Url path
/// segment (for <c>AgenticPension</c>: <c>QWdlbnRpY1BlbnNpb24</c>) to
/// <c>JsonSerializer.Deserialize&lt;string&gt;</c> in <c>LayoutExtensions.GetStream&lt;T&gt;</c> — a bare
/// Base64Url token is not a JSON-quoted string, so it threw and tore down the Blazor circuit. The fix
/// routes node-bound values through <see cref="MeshNodeBindingExtensions"/> (a control-level static
/// extension next to <c>GetMeshNodeStream</c>), reading the field straight off the node.</para>
///
/// <para>These are pure unit tests of that static extension — no Blazor render host, no live mesh.</para>
/// </summary>
public class MeshNodeBindingExtensionsTest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AgenticPension_NodeBoundContext_IsTheQCrashTrigger()
    {
        // This is exactly the DataContext the Settings → Display Description editor binds with.
        var ctx = LayoutAreaReference.GetMeshNodeDataContext("AgenticPension", bindContent: false);
        ctx.Should().Contain("QWdlbnRpY1BlbnNpb24",
            "the node-bound DataContext embeds the Base64Url of the node path");

        // The bare Base64Url segment the OLD layout read path handed to JsonSerializer is NOT valid
        // JSON — this is the 'Q' crash that killed the circuit.
        const string idSegment = "QWdlbnRpY1BlbnNpb24";
        idSegment[0].Should().Be('Q');
        var deserializeIdSegment = () => { JsonSerializer.Deserialize<string>(idSegment); };
        deserializeIdSegment.Should().Throw<JsonException>(
            "a bare Base64Url node-path segment is not valid JSON — this is the crash the seam avoids");
    }

    [Fact]
    public void IsNodeBound_TrueForNodeBoundContext_FalseForData()
    {
        var nodeCtx = LayoutAreaReference.GetMeshNodeDataContext("AgenticPension", bindContent: false);
        MeshNodeBindingExtensions.IsNodeBound(nodeCtx, new JsonPointerReference("Description"))
            .Should().BeTrue();

        MeshNodeBindingExtensions.IsNodeBound("/data/foo", new JsonPointerReference("Description"))
            .Should().BeFalse("an ordinary /data context is resolved through Stream.DataBind, not the node");

        // An absolute pointer is a layout-area path even under a node-bound context.
        MeshNodeBindingExtensions.IsNodeBound(nodeCtx, new JsonPointerReference("/data/foo"))
            .Should().BeFalse();
    }

    [Fact]
    public void ResolveField_WholeNode_ReadsTopLevelDescription()
    {
        // The Settings Display Description editor: bindContent:false, pointer "Description".
        var node = MeshNode.FromPath("AgenticPension") with { Name = "AP", Description = "# Hello" };

        var value = MeshNodeBindingExtensions.ResolveField(
            node, bindContent: false, subPath: null,
            new JsonPointerReference(nameof(MeshNode.Description)), Options);

        value.Should().NotBeNull();
        value!.Value.GetString().Should().Be("# Hello");
    }

    [Fact]
    public void ResolveField_Content_ReadsField_CaseInsensitively_AndUnderSubPath()
    {
        var content = JsonSerializer.Deserialize<JsonElement>(
            """{ "body": "hello", "composer": { "harness": "agentic" } }""");
        var node = MeshNode.FromPath("Space1") with { Content = content };

        // Case-insensitive: a PascalCase pointer matches the camelCase JSON key.
        MeshNodeBindingExtensions.ResolveField(
                node, bindContent: true, subPath: null, new JsonPointerReference("Body"), Options)
            !.Value.GetString().Should().Be("hello");

        // SubPath nests the binding root: "harness" resolves under content/composer.
        MeshNodeBindingExtensions.ResolveField(
                node, bindContent: true, subPath: "composer", new JsonPointerReference("harness"), Options)
            !.Value.GetString().Should().Be("agentic");
    }

    [Fact]
    public void ResolveField_AbsentField_ReturnsNull()
    {
        var node = MeshNode.FromPath("AgenticPension") with { Name = "AP" };

        MeshNodeBindingExtensions.ResolveField(
                node, bindContent: false, subPath: null,
                new JsonPointerReference("DoesNotExist"), Options)
            .Should().BeNull();
    }
}
