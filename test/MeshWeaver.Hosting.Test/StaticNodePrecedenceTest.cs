using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// MeshWeaver#2908 — <b>two readers disagreeing about one path, silently</b>.
///
/// <para>A second static node at a path another contributor already claimed used to be served by
/// <c>StaticNodeQueryProvider</c> and NOT by hub activation
/// (<c>MeshDataSource.WithMeshNodes</c> → <see cref="StaticNodeProviderExtensions.FindServedStaticNode"/>),
/// because each seam implemented its own resolution: the query provider gave the
/// <c>AddMeshNodes</c> seed priority and excluded every other provider's node at a seed-claimed
/// path, while <c>FindServedStaticNode</c> took a bare <c>FirstOrDefault</c> in DI-registration
/// order. Registration order is not a property a host controls, so the same path resolved to
/// different content depending on which way you arrived at it — with no error, no warning and no
/// ambiguity diagnostic.</para>
///
/// <para>These tests pin the two halves of the fix: ONE resolution both readers call, and a LOUD
/// diagnostic when two providers claim a path with different content.</para>
/// </summary>
public class StaticNodePrecedenceTest
{
    private const string Contested = "Admin/HomeConfig";
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>A provider that is not the <c>AddMeshNodes</c> seed — a platform contributor.</summary>
    private sealed class PlatformProvider(params MeshNode[] nodes) : IStaticNodeProvider
    {
        public IEnumerable<MeshNode> GetStaticNodes() => nodes;
    }

    private static MeshNode HomeConfig(string name, bool definitionOnly = false) =>
        MeshNode.FromPath(Contested) with
        {
            NodeType = "Markdown",
            Name = name,
            IsDefinitionOnly = definitionOnly,
        };

    /// <summary>
    /// The #2908 registration exactly: a platform provider registered FIRST (as an
    /// <c>AddPersistence</c>-time provider is), the host's own <c>AddMeshNodes</c> seed second.
    /// </summary>
    private static IServiceProvider BuildContested(MeshNode platformNode, MeshNode seedNode) =>
        new ServiceCollection()
            .AddSingleton<IStaticNodeProvider>(new PlatformProvider(platformNode))
            .AddSingleton<IStaticNodeProvider>(new StaticMeshNodeListProvider([seedNode]))
            .BuildServiceProvider();

    private static StaticNodeQueryProvider QueryProvider(
        IServiceProvider sp, IReadOnlyList<MeshNode> seedNodes, ILoggerFactory? loggerFactory = null) =>
        new(sp.GetServices<IStaticNodeProvider>(),
            _ => true,
            new MeshConfiguration(seedNodes),
            loggerFactory);

    private static IReadOnlyList<MeshNode> QueryPath(StaticNodeQueryProvider provider, string path)
    {
        // The static catalog is purely in-memory: Query returns a completed Observable.Return, so
        // the single emission is already there. No polling, no bridge.
        IReadOnlyList<MeshNode>? items = null;
        using (provider.Query<MeshNode>(new MeshQueryRequest { Query = $"path:{path}" }, Options)
                   .Subscribe(change => items = change.Items))
        {
        }
        items.Should().NotBeNull("StaticNodeQueryProvider emits its snapshot synchronously");
        return items!;
    }

    [Fact]
    public void Both_readers_serve_the_SAME_node_at_a_contested_path()
    {
        var platform = HomeConfig("platform seed");
        var seed = HomeConfig("host override");
        var sp = BuildContested(platform, seed);

        var throughActivation = sp.FindServedStaticNode(Contested);
        var throughQuery = QueryPath(QueryProvider(sp, [seed]), Contested);

        // The substance of #2908: one path, one answer. Before the fix the query seam served the
        // seed while hub activation served the platform provider's node, and nothing said so.
        throughQuery.Should().ContainSingle(
            "a path resolves to exactly one static node, whichever reader asks");
        throughActivation.Should().NotBeNull();
        throughActivation!.Name.Should().Be(throughQuery[0].Name,
            "hub activation and the query provider must resolve the same static node at one path");

        // And the documented rule is that the AddMeshNodes seed wins — the one the host declared.
        throughActivation.Name.Should().Be("host override");
    }

    [Fact]
    public void The_precedence_holds_when_the_seed_hands_the_path_to_persistence()
    {
        // The host declared the path DB-backed (serveFromPartition flips the seed entry to
        // definition-only). A lower-precedence provider still offers a served node there; it must
        // not win the path back — on EITHER seam.
        var platform = HomeConfig("platform seed");
        var seed = HomeConfig("host override", definitionOnly: true);
        var sp = BuildContested(platform, seed);

        sp.FindServedStaticNode(Contested).Should().BeNull(
            "the seed handed this path to the durable row; nothing static serves it");
        sp.ServingStaticProviderName(Contested).Should().BeNull(
            "the collision diagnostic must name the same winner FindServedStaticNode resolves");
        QueryPath(QueryProvider(sp, [seed]), Contested).Should().BeEmpty(
            "a definition-only claim is not a query result, and no provider may serve underneath it");
    }

    [Fact]
    public void A_contested_path_is_reported_LOUD_naming_both_claimants()
    {
        var sp = BuildContested(HomeConfig("platform seed"), HomeConfig("host override"));

        var collisions = sp.DescribeStaticProviderCollisions();

        collisions.Should().ContainSingle("exactly one path is contested");
        collisions[0].Should().Contain(Contested);
        collisions[0].Should().Contain("MeshBuilder.AddMeshNodes", "the winner is named");
        collisions[0].Should().Contain(nameof(PlatformProvider), "the DROPPED claimant is named");
        collisions[0].Should().Contain("2908");

        // …and the durable-collision message a create refusal surfaces says it too, so the append
        // is visible from the seam a host actually hits.
        sp.DescribeStaticServeCollision(Contested).Should()
            .Contain(nameof(PlatformProvider))
            .And.Contain("different content");
    }

    [Fact]
    public void The_query_provider_WARNS_about_a_contested_path_at_construction()
    {
        var sp = BuildContested(HomeConfig("platform seed"), HomeConfig("host override"));
        var factory = new CapturingLoggerFactory();

        QueryProvider(sp, [HomeConfig("host override")], factory);

        factory.Warnings.Should().ContainSingle(
            "a duplicate registration must be LOUD once per mesh, not discovered by comparing views");
        factory.Warnings[0].Should().Contain(Contested).And.Contain(nameof(PlatformProvider));
    }

    [Fact]
    public void Two_providers_offering_the_SAME_declaration_are_redundant_not_contested()
    {
        // Declaring a node type registers it twice by design (AddPartitionType seeds
        // CreateMeshNode() AND registers a provider yielding CreateMeshNode() again). A bare
        // duplicate count would fire on every built-in type and be ignored within a week.
        var sp = BuildContested(HomeConfig("Home"), HomeConfig("Home"));

        sp.DescribeStaticProviderCollisions().Should().BeEmpty(
            "byte-identical declarations are redundant, not a dropped contribution");
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _warnings = [];
        public IReadOnlyList<string> Warnings => _warnings;
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_warnings);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CapturingLogger(List<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                    warnings.Add(formatter(state, exception));
            }
        }
    }
}
