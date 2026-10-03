using System;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Updates.Test;

/// <summary>
/// Scenario 6 — OLD VERSION KEEPS WORKING, in both directions of the content shape. Build N reads
/// a content record <c>ShapeProbe { Title }</c>; build N+1 adds <c>Subtitle</c> (an additive change,
/// the only kind the update contract allows). N+1 code must read N-shaped nodes (the missing field
/// is its default), and N code — still running in a hub nobody recycled, or bound again by a
/// rollback — must read N+1-shaped nodes (the extra field is ignored). Both reads are the REAL
/// compiled builds deserialising the node's stored content, rendered by the instance hub.
/// <para>Falsified by a reader that requires every field it knows (or rejects unknown ones): the
/// N-shaped node renders no title under N+1, or the rollback's read of the N+1-shaped node
/// faults.</para>
/// </summary>
public class ShapeCompatibilityTest(ITestOutputHelper output) : UpdateScenarioBase(output)
{
    private static string ShapeCode(string marker, bool withSubtitle) => $$"""
        using System;
        using System.Linq;
        using System.Reactive.Linq;
        using MeshWeaver.Data;
        using MeshWeaver.Layout.Composition;
        using MeshWeaver.Mesh;
        public record ShapeProbe
        {
            public string? Title { get; init; }
            {{(withSubtitle ? "public string? Subtitle { get; init; }" : "")}}
        }
        public static class UpdateProbeAreas
        {
            public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
                => host.Workspace.GetStream<MeshNode>()!
                    .Select(nodes => nodes?.FirstOrDefault(n => n.Path == host.Hub.Address.ToString()))
                    .Where(n => n is not null)
                    .Select(n => n!.ContentAs<ShapeProbe>(host.Hub.JsonSerializerOptions))
                    .Select(c => (UiControl?)Controls.Html(
                        "<div id='marker'>MARKER_{{marker}}</div>"
                        + "<div id='title'>title=" + c?.Title + ";</div>"
                        {{(withSubtitle ? "+ \"<div id='subtitle'>subtitle=\" + c?.Subtitle + \";</div>\"" : "")}}));
        }
        """;

    [Fact(Timeout = 240_000)]
    public async Task EachBuild_ReadsTheOtherBuildsShape()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var (typePath, n) = await CreateType("N", ShapeCode("N", withSubtitle: false));
        var written = await CreateInstance(typePath, "written-under-n", new { title = "from-N" });
        (await ServedMarker(written)).Should().Contain("MARKER_N<").And.Contain("title=from-N;");

        await EditSource(typePath, ShapeCode("N1", withSubtitle: true));
        await Publish(typePath, n);
        var writtenN1 = await CreateInstance(typePath, "written-under-n1",
            new { title = "from-N1", subtitle = "added-in-N1" });
        (await ServedMarker(writtenN1)).Should().Contain("MARKER_N1<")
            .And.Contain("title=from-N1;").And.Contain("subtitle=added-in-N1;");

        // N+1 reads the N-shaped node (after the dispose that re-binds it).
        await Recycle(written);
        (await ServedMarker(written)).Should().Contain("MARKER_N1<", "N+1 is bound after the dispose")
            .And.Contain("title=from-N;", "the field N wrote is read by N+1")
            .And.Contain("subtitle=;", "the field N never wrote is N+1's default, not a fault");

        // N reads the N+1-shaped node: roll the type back and re-bind.
        await StampBuild(typePath,
            d => d with { RequestedReleasePath = n.LatestReleasePath },
            d => d.RequestedReleasePath == n.LatestReleasePath);
        await Recycle(writtenN1);
        var html = await ServedMarker(writtenN1);
        html.Should().Contain("MARKER_N<", "the rollback binds N");
        html.Should().Contain("title=from-N1;", "N reads the field it knows from an N+1-shaped node");
        html.Should().NotContain("subtitle", "the additive field is ignored by the build that predates it");
    }
}
