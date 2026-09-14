using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 <b>A FAILED compile's result carries the source snapshot Roslyn was handed</b> — the review
/// finding on MeshWeaver#4293 (MeshWeaver#4280, reopened).
///
/// <para>#4293's second half re-drives a type whose failure was formed under a source set the live
/// one has moved past, by comparing <c>Result.CompiledSources</c> with the node's
/// <c>CurrentSourceVersions</c> at the terminal stamp
/// (<see cref="NodeTypeCompileParkRegistry.SourcesMovedSince"/>). The failure branch of
/// <c>MeshNodeCompilationService.CompileAndGetConfigurations</c> destructured the consumed set and
/// then returned a result WITHOUT it — so on the ordinary Roslyn-diagnostic failure (the CS0246
/// both Reinsurance#209 and Plugins#1823 showed) the comparison ran against <c>null</c>, and the
/// re-drive could never fire on the one path it was written for.</para>
///
/// <para>This pins the contract at the seam: a failure result folds the SAME set the compile
/// consumed, under the same rule the success result and the sources watcher use
/// (<see cref="NodeTypeDefinition.SourceVersionOf"/>), so the two snapshots are comparable.</para>
/// </summary>
public class AFailedCompileCarriesTheSetItConsumedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "type/FailedCompileConsumedSet";

    private IMeshNodeCompilationService Compiler =>
        Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>();

    private static MeshNode TypeNode() => MeshNode.FromPath(TypePath) with
    {
        Name = "FailedCompileConsumedSet",
        NodeType = MeshNode.NodeTypePath,
        State = MeshNodeState.Active,
        LastModified = DateTimeOffset.UtcNow,
        Content = new NodeTypeDefinition
        {
            Description = "a type whose source binds a name declared in a sibling that has not landed",
            Configuration = "config => config.WithContentType<RawVariableProbe>()",
        },
    };

    /// <summary>The 8-of-14 shape from Reinsurance run 34804118498: the type's own source binds
    /// <c>PeriodType</c>, declared in an <c>Enums.cs</c> that is still being written.</summary>
    private static IReadOnlyList<MeshNode> PartialSources() =>
    [
        new MeshNode("RawVariable", $"{TypePath}/Source")
        {
            NodeType = "Code",
            Name = "RawVariable",
            State = MeshNodeState.Active,
            LastModified = DateTimeOffset.UtcNow,
            Content = new CodeConfiguration
            {
                Language = "csharp",
                Code = """
                    public record RawVariableProbe { public PeriodType Period { get; init; } }
                    """,
            },
        },
        new MeshNode("Consts", $"{TypePath}/Source")
        {
            NodeType = "Code",
            Name = "Consts",
            State = MeshNodeState.Active,
            LastModified = DateTimeOffset.UtcNow.AddSeconds(-1),
            Content = new CodeConfiguration
            {
                Language = "csharp",
                Code = "public static class RawVariableConsts { public const int One = 1; }",
            },
        },
    ];

    [Fact(Timeout = 300_000)]
    public async Task TheFailureResult_FoldsTheConsumedSetUnderTheSameRuleAsTheLiveSnapshot()
    {
        var sources = PartialSources();

        var failed = await Compiler.CompileAndGetConfigurations(TypeNode(), sources)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the compile must settle — everything below reads its result");

        failed.Should().NotBeNull();
        failed!.AssemblyLocation.Should().BeNullOrEmpty("PeriodType is declared nowhere in this set");
        failed.Diagnostics.Should().Contain(d => d.Id == "CS0246",
            "the failure is the ordinary Roslyn-diagnostic shape the re-drive is written for");

        // 🚨 THE CONTRACT. The consumed set rides on the failure result, folded exactly as the
        // live snapshot is, so the terminal stamp can compare the two.
        failed.CompiledSources.Should().NotBeNull(
            "a failure result that drops the set it had in hand makes SourcesMovedSince compare "
            + "against null, and the re-drive that #4280 added never fires on the CS0246 path");
        failed.CompiledSources!.Keys.OrderBy(k => k, StringComparer.Ordinal).Should().Equal(
            sources.Select(s => s.Path).OrderBy(k => k, StringComparer.Ordinal),
            "it is the set Roslyn was handed — not a fresh discovery");
        foreach (var source in sources)
            failed.CompiledSources[source.Path].Should().Be(NodeTypeDefinition.SourceVersionOf(source),
                "one rule for both snapshots — the sources watcher's CurrentSourceVersions folds "
                + "the same way, which is what makes the two comparable");

        // And the discriminator itself, over exactly these two snapshots: the set the compile
        // consumed is the live set ⇒ a verdict about the code, parks; a live set that has since
        // gained a sibling ⇒ the sources moved during the compile, re-driven.
        var live = failed.CompiledSources;
        NodeTypeCompileParkRegistry.SourcesMovedSince(failed.CompiledSources, live).Should().BeFalse();
        NodeTypeCompileParkRegistry.SourcesMovedSince(
                failed.CompiledSources, live.Add($"{TypePath}/Source/Enums", 638_000_000_000_000_099))
            .Should().BeTrue("Enums.cs landing after the snapshot is what makes the CS0246 stale");
    }
}
