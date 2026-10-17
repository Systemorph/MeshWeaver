using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>#6826, the cause #6872 could not reach: the migration's doc search backfill erased the type
/// of every Doc NodeType in the STORE on every roll.</b>
///
/// <para><b>The chain, end to end.</b> On a portal whose <c>Doc</c> partition is DB-synced (the AKS
/// default, <c>Features:StaticRepoSync:Partitions</c>), <c>doc.mesh_nodes</c> holds the very rows the
/// Doc NodeTypes' activations load. The database-migration Job (MeshWeaver.Plugins,
/// <c>DocumentationBackfill</c>) runs on every roll, reads the embedded docs with
/// <see cref="DocumentationNodeProvider.LoadIndexableNodes"/> WITHOUT a hub, and upserts each row's
/// top-level columns — <c>node_type</c>, <c>name</c>, <c>version</c>, … — keeping only its content.
/// That call's no-options parse read every authored <c>.json</c> node BARE (no NodeType, no name), so
/// the five Doc NodeType rows got <c>node_type = NULL</c>. The row change reaches every running pod
/// over the change feed; each live definition hub, bound to <c>NodeType</c>, saw itself "retyped to
/// (none)" (<c>NodeTypeRebindWatcher.RequiresRebind</c>) and recycled itself; that recycle runs the
/// type's <c>RecycleCascade</c>, whose <see cref="ActivationRecycle.Request"/> reads the STORE — now
/// typeless — and refuses "unknown NodeType(s)". One burst per roll, one line per type, on whichever
/// replica hosted each definition: the production signature. The boot import then rewrote the rows
/// (version floored to 2), which is why every later read found them healthy.</para>
///
/// <para><b>The instrument.</b> A real mesh over a real store (<see cref="InMemoryStorageAdapter"/>)
/// with NO embedded Doc partition — the DB-synced shape, where the store is the only source of a Doc
/// row. The five definitions are seeded as the boot import parses them (the hub's options). The
/// controlled switch is the migration's write, applied the way it lands in production: straight into
/// the store, bypassing every hub, announced post-commit on the change feed. Its decision points are
/// the two the production chain passes through — the type the change event carries (what the rebind
/// watcher compares with the binding) and the verdict of <see cref="ActivationRecycle.Request"/>.</para>
///
/// <para><b>Negative control.</b> The same write with the row's type actually erased is refused with
/// the production string and announces "(none)": the instrument can fail, so its green on the
/// migration's own parse is a finding, not a vacuous pass.</para>
/// </summary>
public class TheDocSearchBackfillKeepsDocNodeTypesTypedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The five NodeTypes the documentation ships — the five refused in production.</summary>
    private static readonly ImmutableArray<string> DocNodeTypes =
    [
        "Doc/Architecture/ATypicalNodeType/Budget",
        "Doc/Architecture/BusinessRules/Cession",
        "Doc/DataMesh/PythonPandasNode/PandasExplorer",
        "Doc/DataMesh/SocialMedia/Post",
        "Doc/DataMesh/SocialMedia/Profile",
    ];

    private readonly InMemoryStorageAdapter store = new();

    /// <inheritdoc />
    protected override IMeshBootstrap Bootstrap => new StoreBootstrap(store);

    private sealed class StoreBootstrap(IStorageAdapter adapter) : IMeshBootstrap
    {
        public string Name => "monolith-over-a-store-backed-doc-partition";

        public MeshBuilder Bootstrap(MeshBuilder builder)
            => builder
                .UseMonolithMesh()
                .ConfigureServices(services => services.AddInMemoryPersistence(adapter))
                .RegisterMeshQueryCoreOnMeshHub();
    }

    /// <summary>
    /// <c>Doc</c> served from the partition rather than from memory — what the portal composes when
    /// the partition is DB-synced: no embedded adapter is registered, so the store is the only
    /// source of a Doc row.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddDocumentation(ImmutableHashSet.Create(StringComparer.Ordinal, DocumentationNodeProvider.RootNamespace));

    /// <summary>
    /// 🚨 THE GUARD, exercised. Options that cannot read the node file format — the case-sensitive
    /// defaults the no-hub parse used before this fix — must make the load THROW, naming the
    /// <c>.json</c> file that read without a NodeType, rather than return the bare node the
    /// migration would write over a live definition row.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void OptionsThatReadANodeFileBare_Throw_NamingTheFile()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        InvalidOperationException? thrown = null;
        IReadOnlyList<MeshNode>? loaded = null;
        try
        {
            loaded = DocumentationNodeProvider.LoadIndexableNodes(new System.Text.Json.JsonSerializerOptions());
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        loaded.Should().BeNull("a load that read a node file bare must not return at all");
        thrown.Should().NotBeNull(
            "case-sensitive options read every camelCase .json node without a NodeType — the bare node "
            + "the guard exists to refuse");
        thrown!.Message.Should().Contain(".json").And.Contain("without a NodeType",
            "the refusal names the offending file, so whoever reads it knows which file or which "
            + "options are wrong");
        var named = DocumentationNodeProvider.LoadIndexableNodes()
            .Where(n => n.Path.StartsWith(DocumentationNodeProvider.RootNamespace + "/", StringComparison.Ordinal))
            .Select(n => n.Path[(DocumentationNodeProvider.RootNamespace.Length + 1)..] + ".json")
            .Where(file => thrown.Message.Contains($"'{file}'", StringComparison.Ordinal))
            .ToImmutableList();
        named.Should().ContainSingle("the message names exactly one real documentation .json file");
    }

    /// <summary>
    /// Pure: the migration's parse (no hub) and the boot import's parse (the hub's options) agree on
    /// every documentation node's identity — above all, on which ones are NodeTypes.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void TheParseWithoutAHub_ReadsEveryDocNodeAsTheImportDoes()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var withoutHub = DocumentationNodeProvider.LoadIndexableNodes()
            .ToImmutableDictionary(n => n.Path, StringComparer.OrdinalIgnoreCase);
        var asImported = DocumentationNodeProvider.LoadIndexableNodes(Mesh.JsonSerializerOptions)
            .ToImmutableDictionary(n => n.Path, StringComparer.OrdinalIgnoreCase);

        withoutHub.Keys.Except(asImported.Keys, StringComparer.OrdinalIgnoreCase).Should().BeEmpty(
            "the migration indexes no page the import does not serve");
        asImported.Keys.Except(withoutHub.Keys, StringComparer.OrdinalIgnoreCase).Should().BeEmpty(
            "the migration indexes every page the import serves");
        foreach (var type in DocNodeTypes)
            withoutHub[type].NodeType.Should().Be(MeshNode.NodeTypePath,
                $"{type} is a NodeType; the migration writes this value into the row its activation loads");

        var disagreements = asImported.Values
            .Where(imported =>
            {
                var read = withoutHub[imported.Path];
                return !string.Equals(read.NodeType, imported.NodeType, StringComparison.Ordinal)
                       || !string.Equals(read.Name, imported.Name, StringComparison.Ordinal)
                       || read.State != imported.State;
            })
            .Select(n => $"{n.Path}: '{withoutHub[n.Path].NodeType ?? "(none)"}'/'{withoutHub[n.Path].Name ?? "(none)"}' "
                         + $"vs '{n.NodeType}'/'{n.Name}'")
            .ToImmutableList();
        disagreements.Should().BeEmpty(
            "every column the migration upserts must read the same as the import wrote it — a "
            + "difference IS a rewrite of the served row on every roll");
    }

    /// <summary>
    /// 🚨 THE #6826 REPRO. The migration's write lands on the five stored definitions: the row keeps
    /// its type, the change feed announces the type the definition hubs are bound to, and the
    /// activation recycle of all five is accepted.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheMigrationsWrite_KeepsEveryDocNodeTypeKnown_AndAnnouncesNoRetype()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsTheBootImportWritesThem();
        (await Recycle(DocNodeTypes)).Refusal.Should().BeNull(
            "control: before the migration writes, the five stored definitions are known");

        var parsed = DocumentationNodeProvider.LoadIndexableNodes()
            .Where(n => DocNodeTypes.Contains(n.Path, StringComparer.OrdinalIgnoreCase))
            .ToImmutableDictionary(n => n.Path, StringComparer.OrdinalIgnoreCase);
        var announced = await WriteAsTheMigration(type => parsed[type]);

        foreach (var type in DocNodeTypes)
            announced[type].Should().Be(MeshNode.NodeTypePath,
                $"{type}'s definition hub is bound to NodeType; any other announced type makes it "
                + "recycle itself and start the cascade that was refused");
        var ticket = await Recycle(DocNodeTypes);
        ticket.Refusal.Should().BeNull("the five rows are still NodeTypes after the migration wrote them");
        ticket.Path.Should().StartWith(ActivationRecycleRequest.Namespace + "/");
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// NEGATIVE CONTROL: the same write with the type actually erased — the row the migration wrote
    /// before the fix — is refused with the production string and announced as a retype to "(none)".
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ABareRowWrittenByTheMigration_IsRefusedAsUnknown_AndAnnouncedAsARetype()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        await SeedAsTheBootImportWritesThem();

        var announced = await WriteAsTheMigration(type => new MeshNode(type.Split('/')[^1], type[..type.LastIndexOf('/')]));

        foreach (var type in DocNodeTypes)
            announced[type].Should().BeNull("the bare row carries no type — the announcement the definition hubs recycle on");
        var ticket = await Recycle(DocNodeTypes);
        ticket.Path.Should().BeNull("nothing is recycled for types the store no longer types");
        ticket.Refusal.Should().Contain("unknown NodeType(s)").And.Contain("Doc/DataMesh/SocialMedia/Post");
    }

    /// <summary>The five definitions as the boot import materializes them — the hub's parse.</summary>
    private async Task SeedAsTheBootImportWritesThem()
    {
        var definitions = new DocumentationStaticRepoSource(Mesh.ServiceProvider).EnumerateSourceNodes()
            .Where(n => DocNodeTypes.Contains(n.Path, StringComparer.OrdinalIgnoreCase))
            // Version 1: the in-memory store is version-conditional (the twin of the adapter's
            // `ON CONFLICT … WHERE version <=` gate), while the migration's raw SQL has no such gate
            // and resets the row to version 1 from ANY version. Seeding at 1 lets the switch land
            // here exactly as it lands in production; the version reset itself is not modelled.
            .Select(n => n with { State = MeshNodeState.Active, Version = 1 })
            .ToImmutableList();
        definitions.Should().HaveCount(DocNodeTypes.Length, "the documentation ships all five");
        definitions.Should().OnlyContain(n => n.NodeType == MeshNode.NodeTypePath);
        foreach (var definition in definitions)
            await store.Write(definition, Mesh.JsonSerializerOptions)
                .Should().Within(TestTimeouts.Convergence)
                .Emit($"{definition.Path} is stored", TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The controlled switch: <c>DocumentationBackfill.UpsertNodeAsync</c>'s
    /// <c>ON CONFLICT DO UPDATE</c> applied to each stored definition — the top-level columns from
    /// <paramref name="parsedAs"/>, version 1, content kept — written straight into the store and
    /// announced post-commit, as the database trigger announces it to every pod. Answers the type
    /// each announcement carried.
    /// </summary>
    private async Task<ImmutableDictionary<string, string?>> WriteAsTheMigration(Func<string, MeshNode> parsedAs)
    {
        var ct = TestContext.Current.CancellationToken;
        var feed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var events = new ReplaySubject<MeshChangeEvent>();
        using var listening = feed.Subscribe(events.OnNext, MeshChangeKind.Updated);

        var announced = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in DocNodeTypes)
        {
            var stored = await store.Read(type, Mesh.JsonSerializerOptions)
                .Should().Within(TestTimeouts.Convergence).Emit($"{type} is read back", ct);
            var parsed = parsedAs(type);
            var upserted = stored! with
            {
                NodeType = parsed.NodeType,
                Name = parsed.Name,
                Description = parsed.Description,
                Category = parsed.Category,
                Icon = parsed.Icon,
                Version = 1,
                State = parsed.State,
                LastModified = DateTimeOffset.UtcNow,
            };
            var committed = await store.WriteAndPublishUpdated(upserted, Mesh.JsonSerializerOptions, feed)
                .Should().Within(TestTimeouts.Convergence).Emit($"the migration's row for {type} is committed", ct);
            // 🚨 The switch must actually LAND: a write the store refused (it answers the stored row
            // instead) would leave the seeded definition in place and pass every case vacuously.
            committed.Should().NotBeNull();
            committed!.Version.Should().Be(1, $"the migration's row for {type} is the one stored");
            committed.Name.Should().Be(parsed.Name, $"the migration's row for {type} is the one stored");
            var change = await events.Where(e => string.Equals(e.Path, type, StringComparison.OrdinalIgnoreCase))
                .Should().Within(TestTimeouts.Convergence).Emit($"the commit of {type} is announced", ct);
            announced[type] = change.NodeType;
        }
        return announced.ToImmutable();
    }

    private Task<ActivationRecycleTicket> Recycle(IEnumerable<string> types) =>
        ActivationRecycle.Request(Mesh, new ActivationRecycleRequest
            {
                NodeTypes = types.ToImmutableList(),
                Reason = "test: #6826 — the Doc NodeTypes survive the migration's search backfill",
                RequestedBy = nameof(TheDocSearchBackfillKeepsDocNodeTypesTypedTest),
            })
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the request answers", TestContext.Current.CancellationToken);
}
