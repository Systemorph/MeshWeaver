using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The batch bake's source-discovery INVARIANT (issue #1216): a pass that cannot establish a type's
/// source set must fail the WHOLE batch, never hand back a per-type verdict.
///
/// <para><b>Why this is the load-bearing half of the fix.</b> An empty source set classifies as
/// <see cref="PreWarmStatus.NoSources"/>, which is deliberately NON-gating — it means "the content
/// was deleted" (#1204). So a broken discovery pass does not look broken: it looks like content
/// breakage. On memex-cloud 2026-08-11 a truncated global fetch reported 169 of 237 types as
/// content-broken, and on a pod whose bake did NOT gate readiness the same pass would have baked a
/// fleet of empty assemblies and stamped every one of them as good, with nothing refusing to
/// serve. Raising the query limit alone fixes the instance; asserting the invariant is what stops
/// the CLASS, because any future way of starving discovery lands here instead of in a verdict.</para>
///
/// <para>The one exception is the genuinely-deleted case, and it needs INDEPENDENT corroboration:
/// the type's own persisted <see cref="NodeTypeDefinition.CurrentSourceVersions"/> snapshot being
/// EXPLICITLY empty. That is the same discriminator
/// <see cref="DynamicTypePreWarmer.ClassifyCompileFailure"/> uses; a NULL snapshot is "the watcher
/// never seeded", which is not evidence of anything and must not be read as one.</para>
/// </summary>
public class NodeTypeBatchBakeDiscoveryInvariantTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "BatchBakeInvariant";

    /// <summary>The ordinary declared-sources shape: rebased to <c>{typePath}/Source</c>.</summary>
    private static readonly IReadOnlyList<string> DeclaredSources = ["namespace:Source scope:subtree"];

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService? Access => Mesh.ServiceProvider.GetService<AccessService>();
    private ILogger? Logger => Mesh.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("BatchBakeInvariant");

    private Task<ImmutableDictionary<string, IReadOnlyList<MeshNode>>> Resolve(
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions)
        => NodeTypeBatchBake
            .ResolveSources(MeshService, Access, definitions, definitions.Keys.ToList(), Logger)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .ToTask();

    /// <summary>
    /// 🚨 The invariant. A type DECLARES source queries, discovery resolves NOTHING for it, and the
    /// type's own snapshot says nothing either — so discovery does not know whether the sources are
    /// gone or whether it simply failed to see them. It must refuse to answer for the whole batch.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ResolveSources_FailsTheWholeBatch_WhenDeclaredSourcesResolveEmptyWithNoCorroboration()
    {
        var typePath = $"{Partition}/Ghost";
        var definitions = new Dictionary<string, NodeTypeDefinition?>(StringComparer.OrdinalIgnoreCase)
        {
            // Declares sources; nothing was ever written under {typePath}/Source. CurrentSourceVersions
            // is null — the type's own watcher never recorded a snapshot, so there is no witness that
            // "no matches" is the mesh's real answer.
            [typePath] = new NodeTypeDefinition
            {
                Configuration = "config => config",
                Sources = DeclaredSources
            }
        };

        var act = () => Resolve(definitions);
        await act.Should()
            .ThrowAsync<NodeTypeBatchBake.SourceDiscoveryFailedException>(
                "a source set that was never established is not a content verdict — the batch must "
                + "abandon and let the activation-driven sweep resolve each type itself")
            .WithMessage($"*{typePath}*");
    }

    /// <summary>
    /// The genuine #1204 case still works: declared sources, a healthy query, genuinely zero
    /// matches — corroborated by an EXPLICITLY empty source snapshot on the type's own record.
    /// Discovery answers normally with an empty set, so the compile that follows can report the
    /// non-gating <see cref="PreWarmStatus.NoSources"/>.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ResolveSources_AcceptsAnEmptySet_WhenTheTypesOwnSnapshotCorroboratesDeletedSources()
    {
        var typePath = $"{Partition}/Deleted";
        var definitions = new Dictionary<string, NodeTypeDefinition?>(StringComparer.OrdinalIgnoreCase)
        {
            [typePath] = new NodeTypeDefinition
            {
                Configuration = "config => config",
                Sources = DeclaredSources,
                // The witness: the type's sources watcher DID run and recorded that there are none.
                CurrentSourceVersions = ImmutableDictionary<string, long>.Empty
            }
        };

        var resolved = await Resolve(definitions);

        resolved[typePath].Should().BeEmpty(
            "deleted content is a real answer when the type's own snapshot corroborates it — this "
            + "is what keeps #1204's non-gating NoSources verdict working");
    }

    /// <summary>
    /// The invariant must not over-fire: a type whose declared sources DO resolve passes straight
    /// through, with the full set. Without this, "abandon the batch" could quietly become the
    /// normal path and batch bake would silently never run.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ResolveSources_ResolvesDeclaredSources_AndDoesNotTripTheInvariant()
    {
        var typePath = $"{Partition}/Healthy";
        for (var i = 0; i < 3; i++)
            await NodeFactory.CreateNode(new MeshNode($"Src{i}", $"{typePath}/Source")
            {
                Name = $"Src{i}",
                NodeType = CodeNodeType.NodeType,
                Content = new CodeConfiguration
                {
                    Code = $"public static class InvariantSrc{i} {{ public const int N = {i}; }}"
                }
            }).Should().Within(30.Seconds()).Emit();

        var definitions = new Dictionary<string, NodeTypeDefinition?>(StringComparer.OrdinalIgnoreCase)
        {
            [typePath] = new NodeTypeDefinition
            {
                Configuration = "config => config",
                Sources = DeclaredSources
            }
        };

        var resolved = await Resolve(definitions);

        resolved[typePath].Should().HaveCount(3,
            "the declared source query matches three Code nodes, so discovery resolves all three");
        // Discovery orders each set by path (Ordinal), so an exact ordered comparison is valid.
        resolved[typePath].Select(n => n.Path).Should().Equal(
            Enumerable.Range(0, 3).Select(i => $"{typePath}/Source/Src{i}"));
    }
}
