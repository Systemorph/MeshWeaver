using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Makes a top-level instance of a partition-owning NodeType declared in MESH CONTENT a whole
/// partition — the creation-side contract <see cref="SpaceNodeType"/>'s handler gives a Space: the
/// creator is granted Admin, and <c>Admin/Partition/{id}</c> is written so the partition routes.
///
/// <para>🚨 <b>It matches STRUCTURALLY</b>, as <see cref="PartitionDropPostDeletionHandler"/> does on
/// the deletion side (#3436), because a NodeType string cannot name a type that is declared in a
/// package (<c>Crm/Client</c>) — no <c>src/</c> registration can enumerate those, including one
/// installed after boot. It matches every partition ROOT whose type is NOT registered in
/// <c>src/</c>; a static owning type (<c>Space</c>, <c>User</c>) brings its own handler, and running
/// this one as well would write a second grant that collides with the first.</para>
///
/// <para>🚨 <b>For a non-System creator, ownership must be POSITIVELY re-established, or the create
/// fails.</b> The validators let a non-System caller create a top-level node only when its type owns
/// a partition, so a declaration that reads "not owning" (or does not answer) here contradicts the
/// decision that let the root be written. Completing quietly would return an ownerless partition —
/// the very defect this handler exists to prevent — so both answers FAULT the create
/// (<see cref="FailsCreateOnError"/>).</para>
///
/// <para>System-created roots are skipped, as <see cref="SpaceNodeType"/> skips them: System needs no
/// grant, and the platform paths that write partition roots as System (import, migration, a package
/// root whose partition the installer provisions) own their partition bookkeeping — they must not
/// gain a second, competing <c>Admin/Partition</c> definition from here. That is also why the
/// definition is written from <see cref="Handle"/>, which knows the creator, and not from
/// <c>GetAdditionalNodes</c>, which does not.</para>
/// </summary>
public sealed class InMeshPartitionOwnerPostCreationHandler(
    IMessageHub hub,
    ILogger<InMeshPartitionOwnerPostCreationHandler>? logger) : INodePostCreationHandler
{
    /// <summary>The diagnostic label: this handler matches STRUCTURALLY, never by type name.</summary>
    public const string AnyInMeshPartitionRoot = "*";

    /// <inheritdoc />
    public string NodeType => AnyInMeshPartitionRoot;

    /// <summary>
    /// The creator's ownership is part of the create's CONTRACT: a partition that persists without
    /// an owner is un-navigable, so a failed grant fails the create (as it does for a Space).
    /// </summary>
    public bool FailsCreateOnError => true;

    /// <summary>A partition root whose type is not registered in <c>src/</c>. Pure, no read.</summary>
    public bool Matches(MeshNode createdNode) =>
        PartitionDefinition.IsPartitionRoot(createdNode)
        && !string.IsNullOrEmpty(createdNode.NodeType)
        && hub.ServiceProvider.FindStaticNode(createdNode.NodeType) is null;

    /// <inheritdoc />
    public IObservable<Unit> Handle(MeshNode createdNode, string? createdBy)
    {
        if (string.Equals(createdBy, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase))
            return Observable.Empty<Unit>();

        return PartitionOwningTypes.OwnsPartition(hub, createdNode.NodeType)
            .SelectMany(owns => owns == true
                ? Establish(createdNode, createdBy)
                : Observable.Throw<Unit>(new InvalidOperationException(Text(
                    "access.partitionCreate.ownerUnestablished", createdNode.Path, createdNode.NodeType))));
    }

    private IObservable<Unit> Establish(MeshNode root, string? createdBy)
    {
        // No creator identity → nobody can be made the owner, and an ownerless partition is exactly
        // the defect this handler exists to prevent. Fail the create (FailsCreateOnError).
        if (string.IsNullOrEmpty(createdBy))
            return Observable.Throw<Unit>(new InvalidOperationException(Text(
                "access.partitionCreate.noCreator", root.Path)));

        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var grant = PartitionOwnership.CreatorAdminGrant(root, createdBy);
        var definition = PartitionOwnership.PartitionDefinitionNode(
            root, $"Partition for {root.NodeType} {root.Name ?? root.Id}");

        // 🚨 RunAsSystem — the SEALED impersonation boundary — never a raw
        // Observable.Using(access.ImpersonateAsSystem, …), which latches System onto the create flow
        // that invoked this handler (#4061; see SpaceGrantScopeDoesNotLatchTheCreateFlowTest). Each
        // write enters its own System scope at Subscribe and leaves it on the way out; Concat keeps
        // them in order — the owner first, then the routing announcement. Both are cross-hub creates
        // composed reactively, the same shape Space's creator grant has always used.
        return access
            .RunAsSystem(() => mesh.CreateNode(grant)
                .Do(_ => logger?.LogInformation(
                    "Granted Admin to {User} on {NodeType} partition {Path}",
                    createdBy, root.NodeType, root.Path)))
            .Concat(access.RunAsSystem(() => mesh.CreateNode(definition)))
            .Select(_ => Unit.Default);
    }

    /// <summary>The failure text in the creator's language — it becomes the create's response.</summary>
    private string Text(string key, params object?[] args) =>
        LocalizationCatalog.Get(key, hub.ServiceProvider.GetService<AccessService>().ViewerLocale(), args);
}
