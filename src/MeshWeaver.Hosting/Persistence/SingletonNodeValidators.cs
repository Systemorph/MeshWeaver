using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// The node-validator set a mesh-wide SINGLETON consults — the query providers' Read filter
/// (<c>StorageAdapterMeshQueryProvider</c>, <c>StaticNodeQueryProvider</c>, and a native backend's) —
/// resolved in a scope THIS holder owns, never in the root container.
///
/// <para>🚨 <b>Why not the root (MeshWeaver#6584).</b> Every <see cref="INodeValidator"/> is registered
/// SCOPED: it runs on the hub that validates the write. A singleton that took
/// <c>IEnumerable&lt;Lazy&lt;INodeValidator&gt;&gt;</c> from the root resolved those scoped services in the
/// root scope — a captive dependency. For the platform's own validators that was one instance in the root
/// scope; for a MODULE's it was worse: the root forwarder had no caller to resolve for, so the module's
/// validator was built in the module container's own root scope, which no caller owns — one instance per
/// generation, shared by every caller, and reported as such by the <c>[ModuleScopeOwnership]</c> diagnostic.
/// Measured on production on 2026-10-22: the Hosting module's <c>InstanceActionRequestWriteGuard</c> and the
/// AI module's <c>ModelCeilingWriteGuard</c>, two write-only validators the Read filter built only to read
/// their <see cref="INodeValidator.SupportedOperations"/> and discard them.</para>
///
/// <para>Resolved here, the scope this holder begins IS the caller: a module's scoped validator is built
/// in the module scope that caller owns, and every instance the scope built is disposed with this holder
/// — with the mesh's root, which owns it as a singleton.</para>
/// </summary>
public sealed class SingletonNodeValidators : IDisposable, IAsyncDisposable
{
    private readonly AsyncServiceScope scope;
    private Lazy<INodeValidator>[] held;

    /// <summary>Begins the owned scope and resolves the validator set in it — lazily: no validator is
    /// built until a consumer asks for <see cref="Lazy{T}.Value"/> (row-level security takes
    /// <c>SecurityService</c>, whose construction reaches the query providers).</summary>
    /// <param name="scopes">The mesh's root scope factory.</param>
    public SingletonNodeValidators(IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        scope = scopes.CreateAsyncScope();
        held = scope.ServiceProvider.GetService<IEnumerable<Lazy<INodeValidator>>>()?.ToArray() ?? [];
        Validators = Enumerate();
    }

    /// <summary>
    /// The mesh's node validators, each built on first use in the scope this holder owns. Read afresh on
    /// every enumeration, and EMPTY once this holder is disposed.
    ///
    /// <para>🚨 A consumer keeps this sequence for its own life, and a consumer can outlive the mesh's root
    /// (the static-node provider stays subscribed to the module registry). Handing it the validator array
    /// itself made every validator the scope built — and the mesh hub each one holds — reachable for as
    /// long as that consumer is: measured by <c>CopyMoveDeadlineReleasesHubTest</c>, where the disposed
    /// mesh hub stayed alive through <c>StaticNodeQueryProvider → Lazy&lt;INodeValidator&gt;[] →
    /// ServicePrincipalAdminGuard → MessageHub</c>. What the scope built ends with the scope.</para>
    /// </summary>
    public IEnumerable<Lazy<INodeValidator>> Validators { get; }

    private IEnumerable<Lazy<INodeValidator>> Enumerate()
    {
        foreach (var validator in Volatile.Read(ref held))
            yield return validator;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Volatile.Write(ref held, []);
        scope.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Volatile.Write(ref held, []);
        return scope.DisposeAsync();
    }
}
