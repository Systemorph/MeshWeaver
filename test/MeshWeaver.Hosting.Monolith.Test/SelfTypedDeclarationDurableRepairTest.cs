using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.AspNetCore.Portal;
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
/// 🚨 A DURABLE self-typed declaration row must be HEALED at startup, not merely refused at the
/// write boundary (#2425 / #2506).
///
/// <para>#2245 retyped the three shipped offenders (<c>User</c>, <c>VUser</c>, <c>Partition</c>)
/// in their STATIC registrations, and #2378 added the write-boundary guard
/// (<c>NodeTypeDeclarationSelfTypingValidator</c>) so no future write can
/// reintroduce the collision. Neither touches a row that was PERSISTED before those fixes: on a
/// store whose durable <c>User</c> row still carries <c>nodeType: "User"</c> with
/// <see cref="NodeTypeDefinition"/> content, every <c>nodeType:User</c> query keeps returning the
/// declaration beside the real accounts — ~600 <c>As&lt;User&gt; for User: value is
/// NodeTypeDefinition</c> errors per hour on production, on an image that already contained both
/// fixes. The row is unreachable through the serve path (the static claim shadows it, #2534), so
/// only a storage-layer repair can correct it — which is what
/// <c>SelfTypedDeclarationDurableRepair</c> does, once, at startup.</para>
///
/// <para>This class boots a mesh whose durable store ALREADY contains the fossil rows — seeded
/// straight into the adapter before the mesh starts, exactly how production got them: written by
/// code that predates the guard.</para>
/// </summary>
public class SelfTypedDeclarationDurableRepairTest : MonolithMeshTestBase
{
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static readonly JsonSerializerOptions SeedOptions = new();

    private readonly InMemoryStorageAdapter persistence = new();

    public SelfTypedDeclarationDurableRepairTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // The pre-#2245 fossil: the durable `User` declaration claiming to BE a user. This is the
        // exact row production still carries (path User, NodeTypeDefinition content,
        // nodeType "User") — written long before the write-boundary guard existed.
        persistence.SaveNodeSynchronously(MeshNode.FromPath("User") with
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

        // The VUser twin — same fossil class, second shipped offender. Pins that the repair is
        // driven by the collision PREDICATE over every static declaration path, not by a
        // hard-coded "User".
        persistence.SaveNodeSynchronously(MeshNode.FromPath("VUser") with
        {
            Name = "VUser",
            NodeType = VUserNodeType.NodeType, // "VUser"
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition(),
            Version = 1,
        }, SeedOptions);

        // A CORRECTLY-typed durable declaration row: must not be gratuitously rewritten.
        persistence.SaveNodeSynchronously(MeshNode.FromPath("Partition") with
        {
            Name = "Partition",
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition(),
            Version = 5,
        }, SeedOptions);

        // A REAL user at a root path (the post-v10 layout): nodeType "User" with User content is
        // an INSTANCE, not a declaration — the repair must leave it alone, and the directory must
        // keep returning it.
        persistence.SaveNodeSynchronously(MeshNode.FromPath("fossilprobe") with
        {
            Name = "Fossil Probe",
            NodeType = UserNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new User { Email = "fossilprobe@meshweaver.io" },
            Version = 1,
        }, SeedOptions);

        // Register the pre-seeded adapter FIRST: AddInMemoryPersistence(adapter) claims the
        // IStorageAdapter slot and sets the core-services marker, so the base configuration's
        // parameterless AddInMemoryPersistence() no-ops instead of registering a second store.
        builder.ConfigureServices(services => services.AddInMemoryPersistence(persistence));
        return base.ConfigureMesh(builder);
    }

    /// <summary>
    /// The heal itself: at startup the fossil rows flip to <c>NodeType = "NodeType"</c> — content,
    /// name and identity preserved, version minted forward — while an already-correct declaration
    /// row and a real user instance are left untouched.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task FossilSelfTypedDeclarationRows_AreRetypedAtStartup()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

        var healedUser = await AwaitHealed(storage, "User", ct);
        healedUser.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions).Should().NotBeNull(
            "the repair retypes the row — it must not clobber the declaration content");
        healedUser.Name.Should().Be("User");
        healedUser.Version.Should().Be(3, "the retype is a forward-minted revision of the row");

        var healedVUser = await AwaitHealed(storage, "VUser", ct);
        healedVUser.Version.Should().Be(2);

        var partition = await storage.ReadAsync("Partition", Mesh.JsonSerializerOptions, ct);
        partition.Should().NotBeNull();
        partition!.NodeType.Should().Be(MeshNode.NodeTypePath);
        partition.Version.Should().Be(5,
            "an already-correct declaration row must not be gratuitously rewritten on every boot");

        var user = await storage.ReadAsync("fossilprobe", Mesh.JsonSerializerOptions, ct);
        user.Should().NotBeNull();
        user!.NodeType.Should().Be(UserNodeType.NodeType,
            "a real user INSTANCE is not a declaration — the repair must never touch it");
        user.Version.Should().Be(1);
    }

    /// <summary>
    /// The consumer the incident was reported from (#2425/#2506): the portal's user directory
    /// query (<see cref="UserIdentityCache.DirectoryQuery"/>, verbatim) no longer returns the
    /// fossil declaration once the repair has run — every row it hands back reads as a
    /// <see cref="User"/>, and the real account seeded beside the fossil is still there.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheUserDirectory_NoLongerReturnsTheFossilDeclaration()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

        // Gate on the heal, not on time — on an unhealed store this times out, which IS the
        // failure being pinned.
        await AwaitHealed(storage, "User", ct);

        var rows = await MeshService.Query<MeshNode>(UserIdentityCache.DirectoryQuery)
            .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Select(c => c.Items)
            .Where(items => items.Any(n =>
                string.Equals(n.Path, "fossilprobe", StringComparison.OrdinalIgnoreCase)))
            .FirstAsync()
            .Timeout(60.Seconds())
            .ToTask(ct);

        rows.Should().NotContain(
            n => string.Equals(n.Path, "User", StringComparison.OrdinalIgnoreCase),
            "the healed declaration row no longer matches nodeType:User, so the user directory "
            + "must stop returning it — every returned row is read as a User by "
            + "UserIdentityCache.TryGetEmail, and the declaration produced one "
            + "'As<User> for User: value is NodeTypeDefinition' error per row per snapshot");

        var notUsers = rows
            .Where(row => row.ContentAs<User>(Mesh.JsonSerializerOptions) is null)
            .Select(row => $"{row.Path} (nodeType:{row.NodeType})")
            .ToList();
        notUsers.Should().BeEmpty("every row of the user directory must read as a User");
    }

    /// <summary>
    /// Waits (bounded) for the durable row at <paramref name="path"/> to carry the corrected
    /// <see cref="MeshNode.NodeTypePath"/> — the repair runs from a hosted service at startup, so
    /// the wait is on the CONDITION, never a fixed sleep.
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
}
