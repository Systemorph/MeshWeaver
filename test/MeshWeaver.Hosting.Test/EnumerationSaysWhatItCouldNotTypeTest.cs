using System.Collections.Generic;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive.Assertions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The bake report's population could be TRUNCATED without anything saying so</b> —
/// the second half of MeshWeaver#3703, and the same defect shape as the first.
///
/// <para><see cref="DynamicTypePreWarmer.DynamicTypesOf"/> used to select the sweep's population
/// with <c>n.Content is NodeTypeDefinition d</c> — a direct CLR pattern match on a payload that
/// crossed a query boundary. A query row's <c>Content</c> is deserialised by whichever hub served
/// it, so an unresolvable <c>$type</c> degrades to a raw <c>JsonElement</c>
/// (<c>Doc/Architecture/SyncedMeshNodeQueries</c>: <i>"a synced query built without the caller's
/// options hands back raw JsonElement and every <c>is T</c> cast fails silently"</i>). Such a
/// NodeType simply VANISHED from the sweep: out of <c>total=</c>, out of <c>baked=</c>, out of
/// <c>pending=</c>. Nothing compiled it, nothing gated on it, and nothing anywhere said a type had
/// been dropped — the report's denominator moved and the report read exactly the same.</para>
///
/// <para>#3703 is a case of two counters being compared across populations one of them could not
/// name. A population that can silently shrink is the same failure one level down, so the read is
/// now <c>ContentAs&lt;NodeTypeDefinition&gt;</c> — which RECOVERS the JSON case rather than
/// dropping it — and whatever still will not type is COUNTED and logged by path. See
/// <c>Doc/Architecture/ControlsThatCannotFail</c>: an instrument must be able to say "I did not
/// check".</para>
///
/// <para>Pure — the function is a projection over an enumeration snapshot, so it needs no mesh.</para>
/// </summary>
public class EnumerationSaysWhatItCouldNotTypeTest
{
    private static NodeTypeDefinition Compilable => new()
    {
        Configuration = "config => config",
        Sources = ["namespace:Source scope:subtree"],
        CompilationStatus = CompilationStatus.Ok,
    };

    /// <summary>
    /// 🚨 THE CONTROL. Content that arrived as JSON — the ordinary cross-hub shape — is RECOVERED
    /// into the population instead of being dropped. Restore the
    /// <c>n.Content is NodeTypeDefinition</c> match and this type is absent from both dictionaries
    /// and from <c>Untyped</c> as well, i.e. gone without a trace, which is exactly what made the
    /// truncation invisible.
    /// </summary>
    [Fact]
    public void ContentThatArrivedAsJson_IsRecovered_NotSilentlyDropped()
    {
        var options = new JsonSerializerOptions();
        var asJson = JsonSerializer.SerializeToElement(Compilable, options);

        var result = DynamicTypePreWarmer.DynamicTypesOf(
            [Node("Doc/Profile", asJson)], options, logger: null);

        result.Definitions.Keys.Should().Equal(
            ["Doc/Profile"],
            "a NodeType whose content crossed a hub boundary as JSON is still a NodeType this "
            + "sweep is responsible for");
        result.Definitions["Doc/Profile"]!.Configuration.Should().Be("config => config");
        result.Untyped.Should().BeEmpty("it typed — there is nothing unchecked here");
    }

    /// <summary>
    /// 🚨 THE OTHER DIRECTION, and the one that makes the count able to fail. Content that CANNOT
    /// be resolved is named rather than absorbed: the sweep decides nothing about it, and the
    /// report says which types those were.
    /// </summary>
    [Fact]
    public void ContentThatCannotBeResolved_IsNamed_SoNothingIsSilentlyUnchecked()
    {
        var options = new JsonSerializerOptions();
        var unresolvable = JsonSerializer.SerializeToElement("this is not a definition", options);

        var result = DynamicTypePreWarmer.DynamicTypesOf(
            [Node("Doc/Profile", Compilable), Node("Doc/Broken", unresolvable)],
            options, logger: null);

        result.Definitions.Keys.Should().Equal(
            ["Doc/Profile"], "only the type that resolved is in the population");
        result.Untyped.Should().Equal(
            ["Doc/Broken"],
            "a type nothing decided anything about must be NAMED — a population that shrinks in "
            + "silence is a denominator nobody can check (#3703)");
    }

    /// <summary>
    /// A STATIC NodeType — one that ships its assembly with the process and has no source to
    /// compile — is out of the population because it was CHECKED and answered "nothing to warm".
    /// It must not land in <see cref="DynamicTypePreWarmer.DynamicTypes.Untyped"/>, or that count
    /// would stop meaning "unchecked" and start meaning "excluded", which is the difference the
    /// whole thing rests on.
    /// </summary>
    [Fact]
    public void ATypeWithNoCompilableSource_IsExcluded_ButIsNotReportedAsUnchecked()
    {
        var options = new JsonSerializerOptions();
        var result = DynamicTypePreWarmer.DynamicTypesOf(
            [Node("Framework/Static", new NodeTypeDefinition())], options, logger: null);

        result.Definitions.Should().BeEmpty();
        result.Untyped.Should().BeEmpty(
            "excluded-because-checked and unchecked are different states and must not share a "
            + "counter");
    }

    /// <summary>A node with no content at all is not an unreadable one — nothing to recover, and
    /// nothing to complain about.</summary>
    [Fact]
    public void ANodeWithNoContent_IsNotReportedAsUnchecked()
    {
        var result = DynamicTypePreWarmer.DynamicTypesOf(
            [Node("Doc/Empty", content: null)], new JsonSerializerOptions(), logger: null);

        result.Definitions.Should().BeEmpty();
        result.Untyped.Should().BeEmpty();
    }

    /// <summary>A node the mesh has DELETED is out of scope for the sweep, as it always was.</summary>
    [Fact]
    public void AnInactiveNode_IsOutOfScope_AndNotReportedAsUnchecked()
    {
        var options = new JsonSerializerOptions();
        var result = DynamicTypePreWarmer.DynamicTypesOf(
            [Node("Doc/Profile", Compilable) with { State = MeshNodeState.Deleted }],
            options, logger: null);

        result.Definitions.Should().BeEmpty();
        result.Untyped.Should().BeEmpty();
    }

    private static MeshNode Node(string path, object? content)
    {
        var slash = path.LastIndexOf('/');
        return new MeshNode(path[(slash + 1)..], path[..slash]) { Content = content };
    }
}
