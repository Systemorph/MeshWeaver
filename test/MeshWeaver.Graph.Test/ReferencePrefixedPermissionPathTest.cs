using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Issue #7090 — a permission check asked about a path written in REFERENCE form
/// (<c>@/Partition/Node</c>, the way a path is spelled in markdown links, autocomplete and every
/// tool argument) took <c>@</c> as the partition. The fold then issued
/// <c>path:@ scope:descendants nodeType:AccessAssignment … limit:all</c> and its <c>_Policy</c> twin;
/// the query parser strips the <c>@</c> (#6828), leaving NO first segment, so the Postgres planner
/// served both reads as a UNION over every partition schema (<c>[FanOut] UNANCHORED</c> on the memex
/// portal). The same mistake made the scope chain <c>@</c>, <c>@/Partition</c>, … — none of which is
/// where a grant lives — so a grant on the real path never applied and the verdict failed CLOSED.
///
/// <para>Pinned on both halves: the census of security reads must name the REAL partition and never
/// <c>@</c>, and a runtime grant at a nested scope must resolve through the reference spelling exactly
/// as it does through the plain one.</para>
/// </summary>
public class ReferencePrefixedPermissionPathTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Viewer = "reference-viewer";
    private const string Partition = "RefSpace";
    private const string Scope = $"{Partition}/Section";
    private const string Target = $"{Scope}/Leaf";

    private readonly SecurityQueryCensus census = new();

    // ConfigureMeshBase, not base.ConfigureMesh: the latter grants every identity Admin everywhere,
    // which short-circuits the fold before it reads anything — the census would be vacuously clean.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureServices(services => services.AddSingleton<IMeshNodeStreamCache>(
                sp => new CensusMeshNodeStreamCache(sp.GetRequiredService<MeshNodeStreamCache>(), census)))
            .AddMeshNodes(new MeshNode(Partition) { Name = "Ref", NodeType = "Markdown" });

    [Theory(Timeout = 120_000)]
    [InlineData("@/" + Target)]
    [InlineData("@" + Target)]
    public async Task AReferencePrefixedPathIsFoldedAsTheMeshPathItNames(string referencePath)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(new MeshNode("Section", Partition) { NodeType = "Markdown", Name = "Section" })
                .Should().Emit();
            await meshService.CreateNode(new MeshNode("Leaf", Scope) { NodeType = "Markdown", Name = "Leaf" })
                .Should().Emit();
            await meshService.CreateNode(AssignmentNodeFactory.UserRole(Viewer, "Viewer", Scope))
                .Should().Emit();
        }

        // The control: the plain mesh path resolves the runtime grant, so the reference arm below
        // cannot fail for an unrelated reason (a grant that never landed, a fold that never answers).
        await Mesh.GetEffectivePermissions(Target, Viewer)
            .Where(p => p.HasFlag(Permission.Read))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        census.Reset();
        var viaReference = await Mesh.GetEffectivePermissions(referencePath, Viewer)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        var asked = census.Queries;
        Output.WriteLine($"#7090 census for '{referencePath}': "
            + string.Join(" | ", asked.OrderBy(x => x, StringComparer.Ordinal)));

        asked.Should().NotContain(q => q.Contains("path:@", StringComparison.Ordinal),
            "'@' is a reference prefix, never a partition — anchoring the fold's partition reads on it "
            + "leaves the planner no first segment, and Postgres serves them as a UNION over every "
            + "partition schema (#7090)");
        asked.Should().Contain(SecurityQueries.PartitionAssignments(Partition),
            $"the reference '{referencePath}' names a node in '{Partition}', so that partition's "
            + "grants are the ones the fold must read");

        viaReference.HasFlag(Permission.Read).Should().BeTrue(
            $"'{referencePath}' names '{Target}', where the viewer holds a Viewer grant — reading the "
            + "chain as '@', '@/RefSpace', … finds no grant and fails the check CLOSED");
    }

    /// <summary>Every query string the security fold hands the stream cache.</summary>
    private sealed class SecurityQueryCensus
    {
        private readonly object gate = new();
        private ImmutableHashSet<string> queries = ImmutableHashSet<string>.Empty;

        public ImmutableHashSet<string> Queries
        {
            get { lock (gate) return queries; }
        }

        public void Record(object id, IEnumerable<string> issued)
        {
            if (id.ToString() is not { } key || !SecurityQueries.IsFoldInput(key))
                return;
            lock (gate) queries = queries.Union(issued);
        }

        public void Reset()
        {
            lock (gate) queries = ImmutableHashSet<string>.Empty;
        }
    }

    /// <summary>A pass-through cache that records the security reads; the mesh under test is real.</summary>
    private sealed class CensusMeshNodeStreamCache(IMeshNodeStreamCache inner, SecurityQueryCensus census)
        : IMeshNodeStreamCache
    {
        public IObservable<MeshNode> GetStream(string path, JsonSerializerOptions options)
            => inner.GetStream(path, options);

        public IObservable<MeshNode> Update(string path, Func<MeshNode, MeshNode> update, JsonSerializerOptions options)
            => inner.Update(path, update, options);

        public IObservable<MeshNode> Overwrite(string path, MeshNode node, JsonSerializerOptions options)
            => inner.Overwrite(path, node, options);

        public void Invalidate(string path) => inner.Invalidate(path);

        public bool ReleaseIfUnwatched(string path) => inner.ReleaseIfUnwatched(path);

        IObservable<IEnumerable<MeshNode>> IMeshNodeStreamCache.GetQuery(
            object id, JsonSerializerOptions options, params string[] queries)
        {
            census.Record(id, queries);
            return inner.GetQuery(id, options, queries);
        }

        IObservable<IEnumerable<MeshNode>>? IMeshNodeStreamCache.GetQuery(object id)
            => inner.GetQuery(id);
    }
}
