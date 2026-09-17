using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A NESTED instance of a partition-owning NodeType declared in mesh CONTENT is refused — and
/// deciding that never activates the type's hub</b> (#4449 item 1).
///
/// <para><b>The defect.</b> <c>OwnsPartitionProvisioningValidator</c> refused a nested instance of an
/// owning type only when the STATIC registry could see the type. For a type declared in mesh content
/// (<c>Crm/Client</c>) it returned <c>Valid()</c> before resolving anything, so
/// <c>acme/somewhere/myclient</c> typed <c>Crm/Client</c> landed as an ordinary child: a data-shape
/// inconsistency the type author's declaration says cannot exist.</para>
///
/// <para><b>The constraint that shapes the fix.</b> The top-level resolver ends in
/// <c>GetMeshNodeStream(&lt;type&gt;)</c>, and for a per-node hub the read IS the activation — a cold
/// compile that can outlast the probe budget and fail closed. On the nested path, which is the
/// ordinary content path, that would be an intermittent refusal of routine work. The nested
/// resolution reads the definition's DURABLE row instead, so each test here asserts STRUCTURALLY
/// that the owning type's hub is still not hosted afterwards (<see cref="HostedHubCreation.Never"/>)
/// — and the premise that it was not hosted before, without which that assertion proves nothing.</para>
///
/// <para>The types are persisted at RUNTIME — never through <c>AddMeshNodes</c>, which would make
/// them static and route every check around the code under test. Security fixture:
/// <see cref="MonolithMeshTestBase.ConfigureMeshBase"/>, so a refusal is a real rule answering and
/// never the public-admin default. Every refusal is asserted by WHAT refused it — its exact keyed
/// sentence — so a timeout or an unrelated fault cannot pass for one.</para>
///
/// <para>Full design: <c>Doc/Architecture/PartitionOwnershipResolution</c>.</para>
/// </summary>
public class InMeshPartitionOwnerNestedCreateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypeLibrary = "nestedownertypes";
    private const string OwnerTypeId = "Partner";
    private const string NonOwnerTypeId = "Widget";
    private const string OwnerType = TypeLibrary + "/" + OwnerTypeId;
    private const string NonOwnerType = TypeLibrary + "/" + NonOwnerTypeId;

    /// <summary>The Space the probe owns, under which every nested create is attempted.</summary>
    private const string ProbeSpace = "nestedprobespace";

    /// <summary>An authenticated identity with no grant anywhere — the ordinary-user shape.</summary>
    private static readonly AccessContext Probe = new() { ObjectId = "nestedprobe", Name = "Nested Probe" };

    /// <summary>The same person, reading the portal in German.</summary>
    private static readonly AccessContext ProbeInGerman = Probe with { Locale = "de" };

    /// <summary>
    /// The one path whose durable READ faults — set by the unknown-case test AFTER seeding, and
    /// null for every other test, so the store is untouched there. Read on the storage adapter's
    /// threads, written on the test's: volatile.
    /// </summary>
    private volatile string? unreadableRow;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        ConfigureMeshBase(builder)
            .ConfigureServices(services =>
            {
                // Wrap the LAST non-keyed IStorageAdapter — the outermost layer every consumer
                // resolves — so the validator's own durable read is what faults in the unknown case.
                var registered = services.Last(d => d.ServiceType == typeof(IStorageAdapter) && !d.IsKeyedService);
                services.Remove(registered);
                return services.AddSingleton<IStorageAdapter>(sp =>
                    new ReadFaultingStorageAdapter(Materialise(registered, sp), path =>
                        string.Equals(path, unreadableRow, StringComparison.OrdinalIgnoreCase)));
            });

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>
    /// The type library — a Space holding one owning and one non-owning NodeType definition,
    /// persisted at runtime as System — and the Space the probe creates and therefore owns.
    /// </summary>
    private async Task Seed(CancellationToken ct)
    {
        await SeedTopLevel(new MeshNode(TypeLibrary)
        {
            Name = "Nested Owner Types",
            NodeType = SpaceNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new Space(),
        });

        foreach (var (id, owns) in new[] { (OwnerTypeId, true), (NonOwnerTypeId, false) })
            await Access.RunAsSystem(() => MeshService.CreateNode(new MeshNode(id, TypeLibrary)
                {
                    Name = id,
                    NodeType = MeshNode.NodeTypePath,
                    State = MeshNodeState.Active,
                    Content = new NodeTypeDefinition { OwnsPartition = owns, DefaultNamespace = owns ? "" : null },
                }))
                .Should().Within(TestTimeouts.CrossSilo).Emit(
                    $"the platform can persist the NodeType definition '{TypeLibrary}/{id}'",
                    cancellationToken: ct);

        await Access.RunAs(Probe, () => MeshService.CreateNode(new MeshNode(ProbeSpace)
            {
                Name = "Nested Probe Space",
                NodeType = SpaceNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new Space(),
            }))
            .Should().Within(TestTimeouts.CrossSilo).Emit(
                "an ordinary user creates a Space and becomes its Admin — the parent every nested "
                + "create below is attempted under, so a refusal there is never a missing grant",
                cancellationToken: ct);

        Mesh.ServiceProvider.FindStaticNode(OwnerType).Should().BeNull(
            "the premise: the owning type is NOT static. If it were, the static registry would "
            + "answer and the in-mesh resolution under test would never run");
    }

    /// <summary>Whether a per-node hub for <paramref name="path"/> is hosted right now — asked
    /// WITHOUT creating one.</summary>
    private bool IsHosted(string path) =>
        Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never) is { IsDisposing: false };

    /// <summary>
    /// The create's failure, asserted to BE a refusal: not a timeout, and naming the node. Anything
    /// else — the budget running out, an unrelated fault — would let a negative control pass while
    /// the rule under test never answered.
    /// </summary>
    private async Task<string> RefusedAs(AccessContext who, MeshNode node, CancellationToken ct)
    {
        var failure = await Record.ExceptionAsync(() => Access.RunAs(who, () => MeshService.CreateNode(node))
            .Take(1)
            .Timeout(TestTimeouts.CrossSilo)
            .Await(ct));

        failure.Should().NotBeNull($"the create of '{node.Path}' must be refused, and it succeeded");
        failure.Should().NotBeOfType<TimeoutException>(
            "a create that never answered is not a refusal — the rule under test did not decide");
        Output.WriteLine($"refusal of '{node.Path}': {failure!.Message}");
        return failure.Message;
    }

    private static MeshNode Nested(string id, string nodeType) => new(id, ProbeSpace)
    {
        Name = id,
        NodeType = nodeType,
        State = MeshNodeState.Active,
    };

    /// <summary>
    /// THE property: a nested instance of an in-mesh owning type is refused with the keyed sentence,
    /// in the caller's language — and the owning type's hub was never activated to decide it. The
    /// same person then creates the same type at the top level, so the refusal is about PLACEMENT,
    /// never about the type or the caller.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ANestedInstanceOfAnInMeshOwningType_IsRefused_WithoutActivatingTheTypesHub()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        IsHosted(OwnerType).Should().BeFalse(
            "the premise of the structural assertion below: seeding must not have hosted the owning "
            + "type's hub, or 'still not hosted afterwards' would be unobservable");

        var nested = Nested("nestedpartner", OwnerType);
        var message = await RefusedAs(Probe, nested, ct);
        message.Should().Contain(
            LocalizationCatalog.Get("access.partitionCreate.nestedOwningType", null,
                nested.Path, OwnerType, nested.Id, nested.Namespace),
            "an instance of a type that owns its partition IS a partition root, so it cannot live "
            + "below the root — refused by the same rule a nested Space meets, now for a type "
            + "declared in mesh content too");

        var german = await RefusedAs(ProbeInGerman, Nested("verschachtelterpartner", OwnerType), ct);
        german.Should().Contain(
            LocalizationCatalog.Get("access.partitionCreate.nestedOwningType", "de",
                $"{ProbeSpace}/verschachtelterpartner", OwnerType, "verschachtelterpartner", ProbeSpace),
            "the refusal is worded in the CALLER's language");

        IsHosted(OwnerType).Should().BeFalse(
            "🚨 THE STRUCTURAL CLAIM. Deciding a nested create read the definition's durable row; "
            + "had it read GetMeshNodeStream(<type>) — the top-level resolver — this hub would now "
            + "be hosted, and on a cold type that read is a compile that can outlast the probe "
            + "budget and refuse routine content intermittently");

        await Access.RunAs(Probe, () => MeshService.CreateNode(new MeshNode("nestedtoppartner")
            {
                Name = "Top Partner",
                NodeType = OwnerType,
                State = MeshNodeState.Active,
            }))
            .Should().Within(TestTimeouts.CrossSilo).Emit(
                "control: the SAME caller creates the SAME type at the top level — so the refusal "
                + "above is about placement, not about the type or who asked",
                cancellationToken: ct);
    }

    /// <summary>
    /// Control: a nested instance of an in-mesh type that does NOT own its partition is unaffected,
    /// and asking about it activates no owning type's hub.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ANestedInstanceOfANonOwningInMeshType_StillLands()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        IsHosted(OwnerType).Should().BeFalse("the premise, as above");

        await Access.RunAs(Probe, () => MeshService.CreateNode(Nested("nestedwidget", NonOwnerType)))
            .Should().Within(TestTimeouts.CrossSilo).Emit(
                "a type whose definition does not declare ownsPartition is ordinary content, and "
                + "creating it under a Space the caller owns is what content creation IS",
                cancellationToken: ct);

        IsHosted(OwnerType).Should().BeFalse(
            "resolving a NON-owning type's declaration touches only that type's row — no owning "
            + "type's hub is warmed to complete a projection");
    }

    /// <summary>
    /// 🚨 The UNKNOWN case. The store cannot answer for the owning type's definition row, so whether
    /// it owns its partition cannot be established: the nested create is refused as UNAVAILABLE —
    /// retryable, never folded into "does not own" (which would let a nested owning instance through
    /// whenever the store hiccups). A create that needs no read — a platform type under the same
    /// parent — is untouched, and so is a non-owning in-mesh type whose row reads fine: the refusal
    /// is scoped to the one type nobody could answer for, not to the parent.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task WhenTheDefinitionRowCannotBeRead_ANestedCreateIsRefusedAsUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        unreadableRow = OwnerType;

        var nested = Nested("unreadablepartner", OwnerType);
        var message = await RefusedAs(Probe, nested, ct);
        message.Should().Contain(
            LocalizationCatalog.Get("access.partitionCreate.undetermined", null,
                OwnerType, PartitionOwningTypes.ProbeTimeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                nested.Path),
            "a durable read that faulted is NOT a verdict: the create is refused as unavailable and "
            + "may be retried — never allowed as though the type did not own its partition");

        await Access.RunAs(Probe, () => MeshService.CreateNode(Nested("stillapage", "Markdown")))
            .Should().Within(TestTimeouts.CrossSilo).Emit(
                "control: a platform type answers from the static registry with no read, so the "
                + "unreadable row cannot touch it",
                cancellationToken: ct);

        await Access.RunAs(Probe, () => MeshService.CreateNode(Nested("stillawidget", NonOwnerType)))
            .Should().Within(TestTimeouts.CrossSilo).Emit(
                "control: an in-mesh type whose row IS readable is decided as before — the refusal "
                + "above is scoped to the type nobody could answer for",
                cancellationToken: ct);
    }

    private static IStorageAdapter Materialise(ServiceDescriptor descriptor, IServiceProvider sp)
        => descriptor.ImplementationFactory is { } factory
            ? (IStorageAdapter)factory(sp)
            : descriptor.ImplementationInstance as IStorageAdapter
              ?? throw new InvalidOperationException(
                  "The IStorageAdapter registration is neither a factory nor an instance, so this "
                  + "test cannot wrap it. Falling back to an unwrapped store would make the unknown-"
                  + "case assertion vacuous.");

    /// <summary>
    /// Faults <see cref="IStorageAdapter.Read"/> (and <see cref="IStorageAdapter.ReadMany"/>) for the
    /// paths <paramref name="faults"/> selects; every other member, and every other path, forwards to
    /// the REAL adapter untouched. Forwarding is exhaustive on purpose: a decorator that falls back to
    /// an interface default silently changes what the store below it does.
    /// </summary>
    private sealed class ReadFaultingStorageAdapter(IStorageAdapter inner, Func<string, bool> faults) : IStorageAdapter
    {
        private static InvalidOperationException Unreachable(string path) =>
            new($"storage read of '{path}' failed: the connection was reset (test-injected)");

        public IObservable<DataChangeNotification> Changes => inner.Changes;

        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => faults(path) ? Observable.Throw<MeshNode?>(Unreachable(path)) : inner.Read(path, options);

        public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
            => paths.FirstOrDefault(faults) is { } path
                ? Observable.Throw<MeshNode>(Unreachable(path))
                : inner.ReadMany(paths, options);

        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options) => inner.Write(node, options);

        public IObservable<IReadOnlyList<MeshNode>> WriteMany(IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
            => inner.WriteMany(nodes, options);

        public IObservable<bool?> WriteIfVersion(MeshNode node, long expectedVersion, JsonSerializerOptions options)
            => inner.WriteIfVersion(node, expectedVersion, options);

        public IObservable<string> Delete(string path) => inner.Delete(path);

        public IObservable<bool> DeleteIfExists(string path) => inner.DeleteIfExists(path);

        public IObservable<IReadOnlyList<string>> DeleteMany(IReadOnlyCollection<string> paths) => inner.DeleteMany(paths);

        public IObservable<string?> FindDeleteBlockingProvider(string path) => inner.FindDeleteBlockingProvider(path);

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
            => inner.ListChildPaths(parentPath);

        public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath) => inner.ListDescendantPaths(rootPath);

        public IObservable<bool> Exists(string path) => inner.Exists(path);

        public IObservable<bool> ExistsInWritableStorage(string path) => inner.ExistsInWritableStorage(path);

        public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(string fullPath, JsonSerializerOptions options)
            => inner.FindBestPrefixMatch(fullPath, options);

        public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(string fullPath, JsonSerializerOptions options)
            => inner.ResolvePath(fullPath, options);

        public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath) => inner.ListPartitionSubPaths(nodePath);

        public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
            => inner.GetPartitionObjects(nodePath, subPath, options);

        public IObservable<Unit> SavePartitionObjects(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => inner.SavePartitionObjects(nodePath, subPath, objects, options);

        public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => inner.DeletePartitionObjects(nodePath, subPath);

        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => inner.GetPartitionMaxTimestamp(nodePath, subPath);
    }
}
