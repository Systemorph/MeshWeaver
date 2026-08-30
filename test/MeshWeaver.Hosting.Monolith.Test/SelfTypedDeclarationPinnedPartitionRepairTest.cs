using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 The durable self-typed <c>User</c> declaration must be healed WHERE ITS INSTANCE QUERY FINDS
/// IT, not only where its path routes (#2641).
///
/// <para><c>SelfTypedDeclarationDurableRepairTest</c> pins the repair on a store where the fossil
/// sits under its own path. memex-cloud is not that store. There, <c>User</c>'s first segment routes
/// to the partition <c>user</c> — which the V27 migration renamed to <c>auth</c> — so a path-routed
/// read of <c>User</c> answers "absent" (the tolerated 42P01) while the row itself sits in the
/// <c>auth</c> schema, exactly where <c>UserNodeType</c> pins every path-less <c>nodeType:User</c>
/// query. Live evidence on the image that carried #2605: <c>search nodeType:User</c> returned the
/// fossil (nodeType <c>User</c>, version 2), <c>search nodeType:User path:User</c> returned nothing
/// (a path switches the Auth pin off), and the repair's boot log carried neither a "retyped" nor a
/// "failed" line — it had read nothing, written nothing and said nothing.</para>
///
/// <para>This class models that store with the in-memory partition stack: a wildcard catch-all plus
/// one specific provider for the <c>Auth</c> partition whose PATH-ROUTED adapter only answers for
/// paths inside <c>Auth/…</c> (what the Postgres path router does with a first segment) and whose
/// PARTITION-SCOPED adapter (<see cref="IPartitionStorageProvider.CreateAdapterForTable"/>, what the
/// partition-storage hubs address a schema through) exposes the whole store. The fossil is seeded
/// into that store — reachable through the partition, invisible to the path.</para>
/// </summary>
public class SelfTypedDeclarationPinnedPartitionRepairTest : MonolithMeshTestBase
{
    private static readonly JsonSerializerOptions SeedOptions = new();

    /// <summary>The <c>auth</c> schema: rows live here by PARTITION, whatever their path says.</summary>
    private readonly InMemoryStorageAdapter authStore = new();

    public SelfTypedDeclarationPinnedPartitionRepairTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // The production fossil, verbatim: path User, namespace "", nodeType "User",
        // NodeTypeDefinition content, version 2 — written into the legacy `user` schema by a
        // first-segment route that no longer exists, carried into `auth` by the V27 rename.
        authStore.SaveNodeSynchronously(MeshNode.FromPath("User") with
        {
            Name = "User",
            NodeType = UserNodeType.NodeType, // "User" — the collision
            Icon = "/static/NodeTypeIcons/person.svg",
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                DefaultNamespace = "",
                RestrictedToNamespaces = [""],
                OwnsPartition = true
            },
            Version = 2,
        }, SeedOptions);

        // A real account, mirrored into auth by the V27 trigger the way every user's root row is
        // (prod: `rbuergi`, nodeType User, version 7). Same partition, same nodeType, NOT a
        // declaration — the repair must leave it exactly as it found it.
        authStore.SaveNodeSynchronously(MeshNode.FromPath("mirroredprobe") with
        {
            Name = "Mirrored Probe",
            NodeType = UserNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new User { Email = "mirroredprobe@meshweaver.io" },
            Version = 7,
        }, SeedOptions);

        // Registered FIRST so the base's parameterless AddInMemoryPersistence() sees the
        // IStorageAdapter slot taken and the core-services marker set, and no-ops.
        builder.ConfigureServices(services =>
        {
            services.AddPartitionedInMemoryPersistence();
            services.AddSingleton<IPartitionStorageProvider>(new AuthPartitionStorageProvider(authStore));
            return services;
        });
        return base.ConfigureMesh(builder);
    }

    /// <summary>
    /// The heal lands in the partition the fossil is in: the <c>auth</c> row flips to
    /// <c>NodeType = "NodeType"</c> with content, name and identity preserved and the version
    /// minted forward; no copy is conjured under the path route; the mirrored real account beside
    /// it is untouched. On the pre-#2641 sweep this times out — the path-routed read never sees
    /// the row.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task FossilInThePinnedPartition_UnreachableByPath_IsRetypedWhereItsInstanceQueryLooks()
    {
        var ct = TestContext.Current.CancellationToken;

        var healed = await AwaitHealed(authStore, "User", ct);
        healed.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions).Should().NotBeNull(
            "the repair retypes the row — it must not clobber the declaration content");
        healed.Name.Should().Be("User");
        healed.Version.Should().Be(3, "the retype is a forward-minted revision of the row");

        var pathRouted = Mesh.ServiceProvider.GetRequiredService<InMemoryStorageAdapter>();
        var strayCopy = await pathRouted.ReadAsync("User", Mesh.JsonSerializerOptions, ct);
        strayCopy.Should().BeNull(
            "the retype must be written through the adapter that FOUND the fossil — a write "
            + "routed by path would land a second User row in the wildcard store and leave the "
            + "auth row self-typed");

        var mirrored = await authStore.ReadAsync("mirroredprobe", Mesh.JsonSerializerOptions, ct);
        mirrored.Should().NotBeNull();
        mirrored!.NodeType.Should().Be(UserNodeType.NodeType,
            "a real user INSTANCE in the same partition is not a declaration — the repair must never touch it");
        mirrored.Version.Should().Be(7);
    }

    /// <summary>
    /// Waits (bounded) for the durable row at <paramref name="path"/> in <paramref name="storage"/>
    /// to carry the corrected <see cref="MeshNode.NodeTypePath"/> — the repair runs from a hosted
    /// service at startup, so the wait is on the CONDITION, never a fixed sleep.
    /// </summary>
    private Task<MeshNode> AwaitHealed(IStorageAdapter storage, string path, CancellationToken ct)
        => Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
            .Where(n => n is not null
                && string.Equals(n.NodeType, MeshNode.NodeTypePath, StringComparison.Ordinal))
            .Select(n => n!)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask(ct);

    /// <summary>
    /// The <c>Auth</c> partition as Postgres shapes it. Path-routed traffic reaches the store only
    /// for paths INSIDE the partition (<c>Auth</c>, <c>Auth/…</c>) — a first segment maps to a
    /// schema and nothing else does — while the partition-scoped adapter addresses the store
    /// directly, whatever the path's first segment. A row filed here under the path <c>User</c> is
    /// therefore reachable through the partition and invisible to the path — the #2641 shape.
    /// </summary>
    private sealed class AuthPartitionStorageProvider(InMemoryStorageAdapter store) : IPartitionStorageProvider
    {
        public string Name => "Auth(test)";
        public bool IsReadOnly => false;
        /// <summary>Durable-backend precedence, like Postgres — asked before the in-memory catch-all.</summary>
        public int Priority => 100;

        public IStorageAdapter Adapter { get; } = new PathFilteringStorageAdapter(store, path =>
            string.Equals(path, "Auth", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("Auth/", StringComparison.OrdinalIgnoreCase));

        public PartitionDefinition? PartitionDefinition { get; } = new()
        {
            Namespace = "Auth",
            Schema = "auth",
        };

        public IStorageAdapter CreateAdapterForTable(PartitionDefinition def, string table) => store;
    }
}
