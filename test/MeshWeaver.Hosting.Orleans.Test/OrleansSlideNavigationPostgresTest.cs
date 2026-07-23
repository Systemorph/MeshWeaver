using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Production-shape (Orleans + partitioned Postgres) coverage of the slide-switch
/// pipeline the portal drives on every deck navigation:
///
/// <list type="number">
///   <item><b>Complete first frame</b> — the FIRST Content frame of a slide must already
///     carry the deck position (counter "Slide n / N", Prev/Next present). The
///     "Slide 1 / 1 then re-render" intermediate frame is the regression this pins.</item>
///   <item><b>Warm deck switch</b> — rendering a SECOND slide of the same deck reuses the
///     shared deck-slides stream: its first frame is complete too.</item>
///   <item><b>Resolution caching</b> — the second resolution of an already-resolved path
///     emits synchronously (the contract the Blazor layer uses to skip progress UI),
///     and a node update (the pre-rendered-HTML derived-cache persist that
///     NavigationService performs on first markdown navigation) invalidates the cached
///     entry so a fresh resolution carries the updated payload.</item>
/// </list>
///
/// <para>Skipped when <c>MESHWEAVER_LOCAL_PG_CS</c> isn't set — same gating and PG
/// wiring as <see cref="OrleansThreadColdLoadHangPostgresTest"/>.</para>
/// </summary>
public class OrleansSlideNavigationPostgresTest(ITestOutputHelper output) : TestBase(output)
{
    private const string ConnectionStringEnvVar = "MESHWEAVER_LOCAL_PG_CS";

    private TestCluster? Cluster { get; set; }
    private IMessageHub ClientMesh => Cluster!.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);
        if (string.IsNullOrEmpty(connectionString))
            return;

        // Bootstrap an EMPTY database (throwaway pgvector container) the way the prod
        // migration does: global schema + partition_access + the ensure_partition_schema
        // proc + the eagerly-created framework schemas. Idempotent — a pre-provisioned
        // local Aspire container is untouched. Mirrors PostgreSqlFixture.InitializeAsync.
        // No UseVector() here: the bootstrap only runs DDL (the initializer creates the
        // vector extension itself); vector CLR mapping is not needed for schema creation.
        var dsBuilder = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
        await using (var dataSource = dsBuilder.Build())
        {
            await PostgreSqlSchemaInitializer.InitializeAsync(dataSource, new PostgreSqlStorageOptions());
            await PostgreSqlSchemaInitializer.InitializePartitionAccessTableAsync(dataSource);
            foreach (var frameworkSchema in new[] { "auth", "system_access" })
            {
                await using var cmd = dataSource.CreateCommand("SELECT public.ensure_partition_schema(@p)");
                cmd.Parameters.AddWithValue("p", frameworkSchema);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        var builder = new TestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.AddSiloBuilderConfigurator<SlideNavPostgresSiloConfigurator>();
        builder.AddClientBuilderConfigurator<TestClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            OrleansClusterDisposal.DisposeInBackground(Cluster);
        await base.DisposeAsync();
    }

    private IMessageHub GetClient(string userPartition, [CallerMemberName] string? name = null)
    {
        var client = ClientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", $"slidenav-pg-{name}-{Guid.NewGuid():N}"),
            config => config.AddLayoutClient());
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = userPartition,
            Name = userPartition,
            Email = $"{userPartition}@meshweaver.io"
        });
        Cluster!.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStream(client.Address, client.DeliverMessage);
        return client;
    }

    /// <summary>
    /// Provisions the user partition schema the platform way — every storage provider's
    /// <see cref="IPartitionStorageProvider.EnsurePartitionProvisioned"/> (PG →
    /// <c>ensure_partition_schema</c> DDL), the same schema-creation a Space/User create
    /// performs. Required on the throwaway test container, which starts empty.
    /// </summary>
    private static async Task ProvisionPartitionAsync(
        IServiceProvider siloSp, string partition, CancellationToken ct)
        => await siloSp.GetServices<IPartitionStorageProvider>()
            .Select(p => p.EnsurePartitionProvisioned(partition))
            .Concat()
            .DefaultIfEmpty(System.Reactive.Unit.Default)
            .LastAsync()
            .ToTask(ct);

    /// <summary>
    /// Seeds a deck (Space parent + three Slide children whose Order contradicts
    /// path-alphabetical order) directly through the silo's storage adapter —
    /// the same PG layout GitSync/portal writes produce.
    /// </summary>
    private async Task<(string Deck, string First, string Middle, string Last)> SeedDeckAsync(
        string userPartition, CancellationToken ct)
    {
        var siloSp = ((InProcessSiloHandle)Cluster!.Primary).SiloHost.Services;
        await ProvisionPartitionAsync(siloSp, userPartition, ct);
        var adapter = siloSp.GetRequiredService<IStorageAdapter>();
        var options = JsonSerializerOptions.Default;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var deckId = $"slidedeck-{suffix}";
        var deckPath = $"{userPartition}/{deckId}";

        await adapter.Write(new MeshNode(deckId, userPartition)
        {
            Name = "PG Slide Deck",
            NodeType = SpaceNodeType.NodeType,
            MainNode = userPartition,
            CreatedBy = userPartition,
            Content = new Space()
        }, options).FirstAsync().ToTask(ct);

        // Created out of order on purpose — Order, not creation or path order, rules.
        var slides = new (string Id, string Name, int Order, string Body)[]
        {
            ("end", "The End", 3, "# Thanks!\n\nQuestions?"),
            ("intro", "Welcome", 1, "# Welcome\n\nTraining time."),
            ("main", "Main", 2, "# The Big Idea\n\n- one\n- two"),
        };
        foreach (var (id, name, order, body) in slides)
            await adapter.Write(new MeshNode(id, deckPath)
            {
                Name = name,
                NodeType = SlideNodeType.NodeType,
                MainNode = userPartition,
                CreatedBy = userPartition,
                Order = order,
                Content = new SlideContent { Content = body, Notes = $"Notes for {name}" }
            }, options).FirstAsync().ToTask(ct);

        Output.WriteLine($"PG deck seeded: {deckPath} (intro/main/end).");
        return (deckPath, $"{deckPath}/intro", $"{deckPath}/main", $"{deckPath}/end");
    }

    /// <summary>
    /// The slide-switch pipeline on PG: first frames are complete (no "Slide 1 / 1"
    /// intermediate), the warm second slide is complete too, and path resolution is
    /// served synchronously from cache on the second ask.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task SlideSwitch_FirstFramesComplete_AndResolutionCached()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ConnectionStringEnvVar)))
        {
            Output.WriteLine($"SKIPPED: set ${ConnectionStringEnvVar} to a running Postgres connection string.");
            return;
        }

        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(110)).Token;
        var userPartition = Environment.GetEnvironmentVariable("MESHWEAVER_LOCAL_USER") ?? "rbuergi";
        var (_, first, middle, last) = await SeedDeckAsync(userPartition, ct);

        var workspace = GetClient(userPartition).GetWorkspace();

        // ── 1. Cold render of the MIDDLE slide: first frame must be complete. ──
        var (counterText, barAreas) = await RenderContentFirstFrame(workspace, middle, ct);
        counterText.Should().Be("Slide 2 / 3",
            "the FIRST rendered counter frame must already carry the deck position — " +
            "an intermediate 'Slide 1 / 1' frame is the flicker regression this test pins");
        barAreas.Should().Contain(a => a.EndsWith("/" + SlideLayoutAreas.PrevButtonArea),
            "the first presenter-bar frame must already have Prev");
        barAreas.Should().Contain(a => a.EndsWith("/" + SlideLayoutAreas.NextButtonArea),
            "the first presenter-bar frame must already have Next");

        // ── 2. Warm switch to the LAST slide: shared deck stream ⇒ complete first frame. ──
        var (lastCounter, lastBar) = await RenderContentFirstFrame(workspace, last, ct);
        lastCounter.Should().Be("Slide 3 / 3", "the warm deck switch must render complete immediately");
        lastBar.Should().Contain(a => a.EndsWith("/" + SlideLayoutAreas.PrevButtonArea));

        // ── 3. Resolution cache: second ask for an already-resolved path is synchronous. ──
        var siloSp = ((InProcessSiloHandle)Cluster!.Primary).SiloHost.Services;
        var resolver = siloSp.GetRequiredService<IPathResolver>();

        var warmup = await resolver.ResolvePath(middle)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask(ct);
        warmup.Should().NotBeNull();
        warmup!.Prefix.Should().Be(middle);

        AddressResolution? synchronous = null;
        using (resolver.ResolvePath(middle).Take(1).Subscribe(r => synchronous = r))
        {
            synchronous.Should().NotBeNull(
                "the second resolution of a cached path must emit synchronously on Subscribe — " +
                "this is the contract the Blazor layer relies on to skip the progress UI");
        }
        synchronous!.Prefix.Should().Be(middle);

        Output.WriteLine("PASSED — first frames complete on cold + warm slides; resolution cache hit is synchronous.");
        _ = first;
    }

    /// <summary>
    /// The pre-rendering leg: the derived-cache persist NavigationService performs on a
    /// markdown node's first navigation (writing <see cref="MeshNode.PreRenderedHtml"/>
    /// back through the node stream) must invalidate the cached path resolution, so the
    /// next navigation's resolution already carries the pre-rendered HTML payload.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task PreRenderedHtmlPersist_InvalidatesResolutionCache()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ConnectionStringEnvVar)))
        {
            Output.WriteLine($"SKIPPED: set ${ConnectionStringEnvVar} to a running Postgres connection string.");
            return;
        }

        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(110)).Token;
        var userPartition = Environment.GetEnvironmentVariable("MESHWEAVER_LOCAL_USER") ?? "rbuergi";

        var siloSp = ((InProcessSiloHandle)Cluster!.Primary).SiloHost.Services;
        await ProvisionPartitionAsync(siloSp, userPartition, ct);
        var adapter = siloSp.GetRequiredService<IStorageAdapter>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var docId = $"slidedoc-{suffix}";
        var docPath = $"{userPartition}/{docId}";

        await adapter.Write(new MeshNode(docId, userPartition)
        {
            Name = "PG prerender doc",
            NodeType = "Markdown",
            MainNode = userPartition,
            CreatedBy = userPartition,
            Content = new MarkdownContent { Content = "# Prerender me\n\nBody." }
        }, JsonSerializerOptions.Default).FirstAsync().ToTask(ct);

        var resolver = siloSp.GetRequiredService<IPathResolver>();
        var cold = await resolver.ResolvePath(docPath)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask(ct);
        cold.Should().NotBeNull();
        cold!.Node.Should().NotBeNull();
        cold.Node!.PreRenderedHtml.Should().BeNull("the doc was seeded without pre-rendered HTML");

        // The durable pre-render persist shape on PG: the adapter does NOT store the
        // node-level PreRenderedHtml field — it derives it ON READ from the
        // MarkdownContent.PrerenderedHtml inside the content jsonb (see
        // PostgreSqlStorageAdapter's read mapping). Persist the content-carried HTML,
        // exactly what authoring / MarkdownContent.Parse produces.
        const string html = "<h1>Prerender me</h1>";
        var client = GetClient(userPartition);
        await client.GetMeshNodeStream(docPath)
            .Update(current => current with
            {
                Content = new MarkdownContent { Content = "# Prerender me\n\nBody.", PrerenderedHtml = html }
            })
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask(ct);

        // The change feed must evict the cached resolution; poll until the fresh
        // resolution carries the pre-rendered payload (pg_notify + feed propagation).
        var refreshed = await Observable
            .Interval(TimeSpan.FromMilliseconds(250))
            .SelectMany(_ => resolver.ResolvePath(docPath).Take(1))
            .Where(r => r?.Node?.PreRenderedHtml is not null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask(ct);
        refreshed!.Node!.PreRenderedHtml.Should().Be(html,
            "an Updated change-feed event must invalidate the cached resolution so navigation " +
            "serves the freshly persisted pre-rendered HTML, not a stale payload");

        Output.WriteLine("PASSED — pre-rendered HTML persist invalidated the resolution cache.");
    }

    /// <summary>
    /// Subscribes the Content area of <paramref name="slidePath"/> and captures the FIRST
    /// frame of the presenter-bar and counter controls (no eventually-settles tolerance —
    /// the first frame IS the assertion).
    /// </summary>
    private static async Task<(string CounterText, string[] BarAreas)> RenderContentFirstFrame(
        IWorkspace workspace, string slidePath, CancellationToken ct)
    {
        var reference = new LayoutAreaReference(SlideLayoutAreas.ContentArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(slidePath), reference);

        var root = (StackControl)(await stream.GetControlStream(reference.Area!)
            .Where(c => c is StackControl)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(45))
            .ToTask(ct))!;

        var barPath = root.Areas
            .Select(a => a.Area?.ToString())
            .First(p => p != null && p.EndsWith("/" + SlideLayoutAreas.PresenterBarArea, StringComparison.Ordinal))!;

        var bar = (StackControl)(await stream.GetControlStream(barPath)
            .Where(c => c is StackControl)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(45))
            .ToTask(ct))!;
        var barAreas = bar.Areas.Select(a => a.Area?.ToString() ?? "").ToArray();

        var counterPath = barAreas.First(p =>
            p.EndsWith("/" + SlideLayoutAreas.CounterArea, StringComparison.Ordinal));
        var counter = (LabelControl)(await stream.GetControlStream(counterPath)
            .Where(c => c is LabelControl)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(45))
            .ToTask(ct))!;

        return (counter.Data?.ToString() ?? "", barAreas);
    }
}

/// <summary>
/// Production-shape PG wiring for the slide-navigation pipeline — the portal mesh
/// (slide node types ship with it) over partitioned Postgres with RLS, same shape as
/// <see cref="ColdLoadPostgresSiloConfigurator"/> minus the AI stack.
/// </summary>
public class SlideNavPostgresSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    private const string ConnectionStringEnvVar = "MESHWEAVER_LOCAL_PG_CS";

    public static readonly string AssemblyStoreRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mw-orleans-slidenav-pg-{Guid.NewGuid():N}");

    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
        siloBuilder.ConfigureServices(services =>
            services.AddFileSystemAssemblyStore(AssemblyStoreRoot));
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVar)
            ?? "Host=localhost;Database=test;Username=postgres;Password=postgres";

        hostBuilder.UseOrleansMeshServer()
            .ConfigureServices(services => services.AddPartitionedPostgreSqlPersistence(connectionString))
            .ConfigurePortalMesh()
            .AddRowLevelSecurity();
    }
}
