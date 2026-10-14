using System;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Updates.Test;

/// <summary>
/// 🚨 <b>A NodeType whose newest compile FAILED still has a schema — the last good build's</b>
/// (Systemorph/MeshWeaver#6635, root cause b).
///
/// <para>The MCP verbs validate content on a probe hub built from the NodeType's configuration,
/// which <c>MeshOperations.ResolveHubConfigForSchema</c> recovers from the build the record names.
/// It waited — bounded at 5 s — for the record to read <c>CompilationStatus == Ok</c>. A record at
/// <c>Error</c> never will until someone fixes the source, so every write to an instance of such a
/// type sat out the full bound (measured on the control instance: writes landing at exactly
/// +5.008 s) and then validated NOTHING, because the probe had no configuration. Yet a failed
/// compile leaves the last good build named on the record, and that build is what the type's
/// instances keep serving (<see cref="NodeTypeUpdateTest.AFailedNewBuild_KeepsTheOldOneServing_AndSaysWhy"/>).</para>
///
/// <para>The discriminating observable is the create-time unknown-member refusal, which only the
/// probe can make for an in-mesh content type: content carrying a member the bound record does
/// not declare is refused, naming it. On the old wait the probe answered "no configuration" after
/// 5 s and the create went through. The control is the same create on build N while it is still
/// <c>Ok</c> — proving the refusal comes from the probe, not from somewhere that never needed it.</para>
/// </summary>
public class SchemaProbeOnAFailedBuildTest(ITestOutputHelper output) : UpdateScenarioBase(output)
{
    /// <summary>Build N: a content record plus the marker area every probe build carries.</summary>
    private static string SchemaCode(string marker) => $$"""
        using MeshWeaver.Layout.Composition;
        public record SchemaProbeContent
        {
            public string? Label { get; init; }
        }
        public static class UpdateProbeAreas
        {
            public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
                => Controls.Html("<div id='marker'>MARKER_{{marker}}</div>");
        }
        """;

    private const string SchemaConfiguration =
        "config => config.WithContentType<SchemaProbeContent>().AddDefaultLayoutAreas()"
        + ".AddLayout(layout => layout.WithView(\"Overview\", UpdateProbeAreas.Overview))";

    /// <summary>An MCP create of an instance whose content carries a member the record lacks.</summary>
    private Task<string> CreateWithUnknownMember(string typePath, string id)
        => new MeshOperations(Mesh)
            .Create(new JsonObject
            {
                ["id"] = id,
                ["namespace"] = typePath,
                ["name"] = id,
                ["nodeType"] = typePath,
                // No "$type": the content stays untyped on the writing hub, so only the probe —
                // which binds the NodeType's own record — can judge its members.
                ["content"] = new JsonObject { ["label"] = "declared", ["undeclaredMember"] = 1 },
            }.ToJsonString())
            .FirstAsync().Timeout(Step)
            .Await(TestContext.Current.CancellationToken);

    [Fact(Timeout = 240_000)]
    public async Task AFailedNewBuild_StillValidatesAgainstTheLastGoodBuild_WithoutWaitingForOk()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var (typePath, n) = await CreateType("N", SchemaCode("N"), SchemaConfiguration);

        // Control: on a healthy build the probe refuses the undeclared member.
        var onOk = await CreateWithUnknownMember(typePath, "onok");
        Output.WriteLine($"build N (Ok): {onOk}");
        onOk.Should().Contain("undeclaredMember",
            "the probe binds the NodeType's own record, so an undeclared member is refused by name — "
            + "without this the assertion below would test nothing");

        await EditSource(typePath, BrokenCode);
        var failed = await Publish(typePath, n);
        failed.CompilationStatus.Should().Be(CompilationStatus.Error, "N+1 does not compile");
        failed.LatestAssemblyPath.Should().Be(n.LatestAssemblyPath,
            "a failed compile leaves the last good build named — the build the probe must use");

        var clock = Stopwatch.StartNew();
        var onError = await CreateWithUnknownMember(typePath, "onerror");
        clock.Stop();
        Output.WriteLine($"build N+1 (Error), {clock.ElapsedMilliseconds} ms: {onError}");

        onError.Should().Contain("undeclaredMember",
            "the record is at Error but still names build N, which every instance of the type keeps "
            + "serving; the schema is that build's — waiting for an Ok that cannot come and then "
            + "validating nothing let the undeclared member through (#6635)");
        onError.Should().NotStartWith("Created:", "the refusal writes nothing");
    }
}
