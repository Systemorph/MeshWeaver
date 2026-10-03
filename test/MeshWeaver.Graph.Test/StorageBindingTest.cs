using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Storage;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Storage;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Storage bindings (<c>Doc/Architecture/StorageBindings</c>): a typed node per binding in the
/// partition it belongs to (<c>{partition}/_Storage/{id}</c>, global = <c>Admin/_Storage</c>), the
/// select-or-create flow inside the instance's PRE-CONFIGURED store, the verdict recorded on the
/// node by the binding's own hub, the resolver every storage consumer asks, and who may see and edit
/// what.
///
/// <para>The store is a REAL <see cref="DirectoryInstanceStore"/> over a temp directory — its
/// containers are directories that exist or do not — never a stand-in. Identities are real grants
/// (<c>ConfigureMeshBase</c>, no public admin): a platform admin holding exactly Admin on
/// <c>Admin</c>, a partition admin holding Admin on <c>acme</c>, and an ordinary user holding only
/// their own partition.</para>
/// </summary>
public class StorageBindingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PlatformAdmin = "storage-platform-admin";
    private const string AcmeAdmin = "storage-acme-admin";
    private const string Ordinary = "storage-ordinary";
    private const string Acme = "acme";
    private const string Globex = "globex";
    private const string StoreId = "files";

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-storage-" + Guid.NewGuid().ToString("N")[..8]);

    private static TimeSpan Budget => TestTimeouts.Convergence;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(root);
        // The store's existing containers: acme's own default, one named for acme, and globex's.
        Directory.CreateDirectory(Path.Combine(root, "acme"));
        Directory.CreateDirectory(Path.Combine(root, "acme_parts"));
        Directory.CreateDirectory(Path.Combine(root, "globex"));
        return ConfigureMeshBase(builder)
            .ConfigureServices(services => services.AddSingleton<IInstanceStore>(sp => new DirectoryInstanceStore(
                StoreId, "Test files", root, [StoragePurpose.DocParts, StoragePurpose.DurableStream],
                sp.GetRequiredService<IoPoolRegistry>())))
            .AddMeshNodes(
                UserNode(PlatformAdmin), UserNode(AcmeAdmin), UserNode(Ordinary),
                new MeshNode(Acme) { Name = "Acme", NodeType = "Space" },
                new MeshNode(Globex) { Name = "Globex", NodeType = "Space" },
                AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", AdminAppNodeType.Path),
                AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", PlatformAdmin),
                AssignmentNodeFactory.UserRole(AcmeAdmin, "Admin", Acme),
                AssignmentNodeFactory.UserRole(AcmeAdmin, "Admin", AcmeAdmin),
                AssignmentNodeFactory.UserRole(Ordinary, "Admin", Ordinary));
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    private static MeshNode UserNode(string id) => new(id)
    {
        NodeType = UserNodeType.NodeType,
        Name = id,
        State = MeshNodeState.Active,
        Content = new User { FullName = id, Email = $"{id}@meshweaver.io" },
    };

    private static AccessContext Identity(string userId)
        => new() { ObjectId = userId, Name = userId, Email = $"{userId}@meshweaver.io" };

    private void ActAs(string userId)
    {
        TestUsers.DevLogin(Mesh, Identity(userId));
        Mesh.ServiceProvider.GetRequiredService<AccessService>().SetContext(Identity(userId));
    }

    private IInstanceStore Store => Mesh.ServiceProvider.GetServices<IInstanceStore>().Single(s => s.Id == StoreId);

    private IStorageBindingResolver Resolver => Mesh.ServiceProvider.GetRequiredService<IStorageBindingResolver>();

    private Task<MeshNode> Create(string userId, string partition, string id, StorageBinding binding, CancellationToken ct)
    {
        ActAs(userId);
        return Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(new MeshNode(id, StorageBindingPaths.NamespaceOf(partition))
            {
                NodeType = StorageBindingPaths.NodeType,
                Name = id,
                MainNode = partition,
                Content = binding,
            })
            .FirstAsync().Timeout(Budget).Await(ct);
    }

    /// <summary>The binding as its own hub records it, once <paramref name="until"/> holds. Reading it
    /// is also what activates the binding's hub — a written node is not processed until something
    /// reads it.</summary>
    private Task<StorageBinding> Binding(string path, Func<StorageBinding, bool> until, CancellationToken ct)
        => Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .Select(n => n!.ContentAs<StorageBinding>(Mesh.JsonSerializerOptions))
            .Where(b => b is not null && until(b))
            .Select(b => b!)
            .FirstAsync().Timeout(Budget).Await(ct);

    private Task<ResolvedStorage> Resolve(string purpose, string partition, Func<ResolvedStorage, bool> until, CancellationToken ct)
        => Resolver.Resolve(purpose, partition).Where(until).FirstAsync().Timeout(Budget).Await(ct);

    // ── The pre-configured store ────────────────────────────────────────────────────────────────

    /// <summary>The store lists the containers that exist in it, live, and a partition is offered
    /// only its own: its default container and the ones named for it — never another partition's.</summary>
    [Fact(Timeout = 60000)]
    public async Task TheStore_ListsItsExistingContainers_AndAPartitionIsOfferedOnlyItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        var all = await Store.ListContainers().Timeout(Budget).Await(ct);
        all.Should().Equal("acme", "acme_parts", "globex");

        StorageContainerOwnership.Usable(Store, Acme, all).Should().Equal("acme", "acme_parts");
        StorageContainerOwnership.Usable(Store, StorageBindingPaths.AdminPartition, all)
            .Should().Equal(["acme", "acme_parts", "globex"], "the instance's own bindings may use any container");
    }

    /// <summary>Creating a container creates it once; the second call for the same name finds it and
    /// creates nothing.</summary>
    [Fact(Timeout = 60000)]
    public async Task CreatingAContainer_IsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await Store.EnsureContainer("acme_new").Timeout(Budget).Await(ct);
        first.Should().Be(new StorageProbe("acme_new", Reachable: true, Exists: true, Created: true));
        Directory.Exists(Path.Combine(root, "acme_new")).Should().BeTrue();

        var second = await Store.EnsureContainer("acme_new").Timeout(Budget).Await(ct);
        second.Should().Be(new StorageProbe("acme_new", Reachable: true, Exists: true, Created: false),
            "the second create of the same name finds it and creates nothing");
        (await Store.ListContainers().Timeout(Budget).Await(ct)).Should().ContainSingle(c => c == "acme_new");
    }

    /// <summary>A malformed name is refused by the store's own rule and never reaches the store.</summary>
    [Theory(Timeout = 60000)]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("-leading")]
    public async Task AnInvalidName_IsRefused_AndNothingIsCreated(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Store.ListContainers().Timeout(Budget).Await(ct);
        var probe = await Store.EnsureContainer(name).Timeout(Budget).Await(ct);

        probe.NameRefused.Should().BeTrue();
        probe.Exists.Should().BeFalse();
        (await Store.ListContainers().Timeout(Budget).Await(ct)).Should().Equal(before);
        Directory.Exists(Path.Combine(root, "escape")).Should().BeFalse();
    }

    // ── Defaults ────────────────────────────────────────────────────────────────────────────────

    /// <summary>With zero bindings everything resolves to what the instance does anyway: the
    /// store registered as the purpose's default, in the partition's own container; and a purpose no
    /// store serves resolves to the instance's built-in storage.</summary>
    [Fact(Timeout = 60000)]
    public async Task ZeroBindings_ResolveToTheInstanceDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        var parts = await Resolve(StoragePurpose.DocParts, Acme, _ => true, ct);
        parts.IsDefault.Should().BeTrue();
        parts.Should().Be(new ResolvedStorage(StoragePurpose.DocParts, Acme, StoreId, StorageStoreKind.Directory, "acme"));

        var originals = await Resolve(StoragePurpose.Originals, Acme, _ => true, ct);
        originals.IsDefault.Should().BeTrue();
        originals.StoreKind.Should().Be(StorageStoreKind.Instance, "no pre-configured store serves originals here");
    }

    // ── Select, create, record ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A partition admin binds their doc parts to an EXISTING container picked from the store; the
    /// binding's hub validates it and records the verdict on the node, and the resolver answers it.
    /// Then they CREATE a new one: the hub creates the container with the instance's identity and
    /// the binding records it as its container.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task APartitionAdmin_PicksOrCreatesAContainer_AndTheBindingRecordsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var node = await Create(AcmeAdmin, Acme, "parts",
            new StorageBinding { Purpose = StoragePurpose.DocParts, StoreId = StoreId, Container = "acme_parts" }, ct);
        node.Path.Should().Be("acme/_Storage/parts", "a binding lives in the partition it belongs to");

        var picked = await Binding(node.Path, b => b.ValidationStatus != StorageValidationStatus.Pending, ct);
        picked.ValidationStatus.Should().Be(StorageValidationStatus.Valid, picked.ValidationMessage ?? "");
        picked.Container.Should().Be("acme_parts");
        picked.ValidatedTarget.Should().Be(picked.TargetKey());
        picked.ValidatedAt.Should().NotBeNull();

        var resolved = await Resolve(StoragePurpose.DocParts, Acme, r => !r.IsDefault, ct);
        resolved.Should().Be(new ResolvedStorage(StoragePurpose.DocParts, Acme, StoreId, StorageStoreKind.Directory, "acme_parts")
        { BindingPath = node.Path });

        // Create a new container from the same binding.
        ActAs(AcmeAdmin);
        await Mesh.GetWorkspace().GetMeshNodeStream(node.Path).Update<StorageBinding>(b => b with
            {
                NewContainerName = "acme_fresh",
                RequestedAction = StorageBindingAction.Create,
                RequestedAt = DateTimeOffset.UtcNow,
            })
            .FirstAsync().Timeout(Budget).Await(ct);

        var created = await Binding(node.Path, b => b.Container == "acme_fresh" && b.RequestedAction is null, ct);
        created.ValidationStatus.Should().Be(StorageValidationStatus.Valid, created.ValidationMessage ?? "");
        created.NewContainerName.Should().BeNull();
        Directory.Exists(Path.Combine(root, "acme_fresh")).Should().BeTrue("the hub created it in the store");
        (await Resolve(StoragePurpose.DocParts, Acme, r => r.Container == "acme_fresh", ct)).BindingPath.Should().Be(node.Path);
    }

    /// <summary>A partition cannot bind another partition's container: the verdict is Invalid and the
    /// resolver keeps the default.</summary>
    [Fact(Timeout = 120000)]
    public async Task APartition_CannotBindAnotherPartitionsContainer()
    {
        var ct = TestContext.Current.CancellationToken;
        var node = await Create(AcmeAdmin, Acme, "steal",
            new StorageBinding { Purpose = StoragePurpose.VectorIndex, StoreId = StoreId, Container = "globex" }, ct);

        var verdict = await Binding(node.Path, b => b.ValidationStatus != StorageValidationStatus.Pending, ct);
        verdict.ValidationStatus.Should().Be(StorageValidationStatus.Invalid);
        verdict.ValidationMessage.Should().Contain("not this partition's");
        (await Resolve(StoragePurpose.VectorIndex, Acme, _ => true, ct)).IsDefault.Should().BeTrue();
    }

    /// <summary>A binding that does not validate never overrides: a container named for the partition
    /// that does not exist in the store is Invalid, and the resolver keeps the default.</summary>
    [Fact(Timeout = 120000)]
    public async Task AnInvalidBinding_DoesNotOverride()
    {
        var ct = TestContext.Current.CancellationToken;
        var node = await Create(AcmeAdmin, Acme, "missing",
            new StorageBinding { Purpose = StoragePurpose.DocParts, StoreId = StoreId, Container = "acme_missing" }, ct);

        var verdict = await Binding(node.Path, b => b.ValidationStatus != StorageValidationStatus.Pending, ct);
        verdict.ValidationStatus.Should().Be(StorageValidationStatus.Invalid);
        verdict.ValidationMessage.Should().Contain("does not exist");
        Directory.Exists(Path.Combine(root, "acme_missing")).Should().BeFalse("validation never creates");
        (await Resolve(StoragePurpose.DocParts, Acme, _ => true, ct)).IsDefault.Should().BeTrue();
    }

    /// <summary>A verdict written onto the node by hand cannot widen what a partition may bind: the
    /// resolver re-applies the ownership rule.</summary>
    [Fact(Timeout = 120000)]
    public async Task AForgedVerdict_DoesNotWidenWhatAPartitionMayBind()
    {
        var ct = TestContext.Current.CancellationToken;
        var forged = new StorageBinding { Purpose = StoragePurpose.Originals, StoreId = StoreId, Container = "globex" };
        forged = forged with
        {
            ValidationStatus = StorageValidationStatus.Valid,
            ValidatedTarget = forged.TargetKey(),
        };
        // Written straight into the resolver's rule, so the hub's re-validation cannot be what saves it.
        var nodes = ImmutableList.Create(new MeshNode("forged", StorageBindingPaths.NamespaceOf(Acme))
        {
            NodeType = StorageBindingPaths.NodeType,
            Content = forged,
        });
        var picked = StorageBindingResolver.Pick(Mesh, [Store], StoragePurpose.Originals, Acme, null, nodes, []);
        picked.IsDefault.Should().BeTrue("globex is not acme's container, whatever the node claims");

        // The control: the same forged verdict on a container acme owns IS honoured.
        var owned = forged with { Container = "acme_parts" };
        owned = owned with { ValidatedTarget = owned.TargetKey() };
        StorageBindingResolver.Pick(Mesh, [Store], StoragePurpose.Originals, Acme, null,
                [nodes[0] with { Content = owned }], [])
            .Container.Should().Be("acme_parts");
    }

    // ── Global (Admin) bindings ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The instance's global bindings live under Admin. An instance-wide purpose (durable streams)
    /// of a partition with no binding of its own falls back to Admin's; a per-partition purpose
    /// (doc parts) never does.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AdminBindings_AreTheGlobalOnes_AndOnlyInstanceWidePurposesFallBackToThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var node = await Create(PlatformAdmin, StorageBindingPaths.AdminPartition, "streams",
            new StorageBinding { Purpose = StoragePurpose.DurableStream, StoreId = StoreId, Container = "globex" }, ct);
        node.Path.Should().Be("Admin/_Storage/streams");
        var verdict = await Binding(node.Path, b => b.ValidationStatus != StorageValidationStatus.Pending, ct);
        verdict.ValidationStatus.Should().Be(StorageValidationStatus.Valid, verdict.ValidationMessage ?? "");

        var streams = await Resolve(StoragePurpose.DurableStream, Acme, r => !r.IsDefault, ct);
        streams.BindingPath.Should().Be(node.Path);
        streams.Partition.Should().Be(Acme);
        streams.Container.Should().Be("globex");

        // Admin binds doc parts too — and it still never decides a partition's doc parts.
        var adminParts = await Create(PlatformAdmin, StorageBindingPaths.AdminPartition, "parts",
            new StorageBinding { Purpose = StoragePurpose.DocParts, StoreId = StoreId, Container = "globex" }, ct);
        (await Binding(adminParts.Path, b => b.ValidationStatus != StorageValidationStatus.Pending, ct))
            .ValidationStatus.Should().Be(StorageValidationStatus.Valid);
        (await Resolve(StoragePurpose.DocParts, StorageBindingPaths.AdminPartition, r => !r.IsDefault, ct))
            .BindingPath.Should().Be(adminParts.Path, "the control: Admin's own doc parts ARE bound");
        (await Resolve(StoragePurpose.DocParts, Acme, _ => true, ct)).IsDefault.Should().BeTrue(
            "doc parts are per partition — Admin's bindings never decide them");
    }

    // ── Access ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>An ordinary user may not write the instance's bindings, nor another partition's; the
    /// partition admin may write their own (the control).</summary>
    [Fact(Timeout = 120000)]
    public async Task ANonAdmin_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var intoAdmin = () => Create(Ordinary, StorageBindingPaths.AdminPartition, "intrude",
            new StorageBinding { Purpose = StoragePurpose.DurableStream }, ct);
        await intoAdmin.Should().ThrowAsync<Exception>("only a platform admin administers Admin");

        var intoAcme = () => Create(Ordinary, Acme, "intrude", new StorageBinding(), ct);
        await intoAcme.Should().ThrowAsync<Exception>("only acme's administrators administer acme's storage");

        var own = await Create(AcmeAdmin, Acme, "own", new StorageBinding(), ct);
        own.Path.Should().Be("acme/_Storage/own", "the control: acme's admin writes acme's bindings");
    }

    /// <summary>
    /// A platform admin holds NO data access by that role: they cannot read a partition's bindings
    /// (neither listed nor read by path), while the partition's own admin lists them.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AGlobalAdminWithoutGrants_CannotReadAPartitionsBindings()
    {
        var ct = TestContext.Current.CancellationToken;
        var node = await Create(AcmeAdmin, Acme, "secret-ish",
            new StorageBinding { Purpose = StoragePurpose.DocParts, StoreId = StoreId, Container = "acme_parts" }, ct);

        ActAs(AcmeAdmin);
        (await Query(StorageBindingPaths.QueryFor(Acme), rows => rows.Count > 0, ct))
            .Select(n => n.Path).Should().Contain(node.Path, "the control: acme's admin lists acme's bindings");

        ActAs(PlatformAdmin);
        (await Query(StorageBindingPaths.QueryFor(Acme), _ => true, ct))
            .Should().BeEmpty("a global admin has no data access to acme");
        var pointRead = () => Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>()
            .GetStream(node.Path, Mesh.JsonSerializerOptions)
            .FirstAsync().Timeout(TestTimeouts.Quick).Await(ct);
        await pointRead.Should().ThrowAsync<Exception>("nor may they read it by its path");

        var rule = Mesh.ServiceProvider.GetServices<INodeTypeAccessRule>().Single(r => r.NodeType == StorageBindingPaths.NodeType);
        (await rule.HasAccess(new NodeValidationContext
            {
                Operation = NodeOperation.Read, Node = node, AccessContext = Identity(PlatformAdmin),
            }, PlatformAdmin).FirstAsync().Timeout(Budget).Await(ct))
            .Should().BeFalse("the binding's own rule asks for Update on acme, which the platform admin lacks");
        (await rule.HasAccess(new NodeValidationContext
            {
                Operation = NodeOperation.Read, Node = node, AccessContext = Identity(AcmeAdmin),
            }, AcmeAdmin).FirstAsync().Timeout(Budget).Await(ct))
            .Should().BeTrue("the control: acme's admin holds it");
    }

    private Task<IReadOnlyList<MeshNode>> Query(string query, Func<IReadOnlyList<MeshNode>, bool> until, CancellationToken ct)
        => Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset or QueryChangeType.Added)
            .Select(c => (IReadOnlyList<MeshNode>)c.Items.ToList())
            .Where(until)
            .FirstAsync().Timeout(Budget).Await(ct);

    // ── The settings section ────────────────────────────────────────────────────────────────────

    /// <summary>The Admin app carries a Storage tab for a platform admin, and it lists the instance's
    /// pre-configured stores.</summary>
    [Fact(Timeout = 120000)]
    public async Task TheInstanceSettings_ShowTheStorageSection_WithThePreConfiguredStores()
    {
        var ct = TestContext.Current.CancellationToken;
        ActAs(PlatformAdmin);
        await GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(AdminAppNodeType.Path),
                new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea) { Id = StorageSettingsTab.TabId })
            .Select(change => change.Value.GetRawText())
            .Should().Within(Budget)
            .Match(json => json.Contains("\"title\":\"Storage\"") && json.Contains("Test files") && json.Contains(StoreId),
                "the tab is offered and lists the store", ct);
    }

    /// <summary>A binding's own page offers, as container choices, exactly the containers its
    /// partition may bind — read live from the store — and never another partition's.</summary>
    [Fact(Timeout = 120000)]
    public async Task TheBindingsOwnPage_OffersOnlyThePartitionsContainers()
    {
        var ct = TestContext.Current.CancellationToken;
        var node = await Create(AcmeAdmin, Acme, "page",
            new StorageBinding { Purpose = StoragePurpose.DocParts, StoreId = StoreId }, ct);

        ActAs(AcmeAdmin);
        var page = await GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(node.Path),
                new LayoutAreaReference(StorageBindingLayoutArea.AreaName))
            .Select(change => change.Value.GetRawText())
            .Should().Within(Budget)
            .Match(json => json.Contains("\"acme_parts\"") && json.Contains("newContainerName"),
                "the editor is bound to the node and offers acme's containers", ct);

        page.Should().NotContain("\"globex\"", "globex's container is never offered to acme");
    }
}
