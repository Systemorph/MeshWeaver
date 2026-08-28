using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Heals, once at startup, the DURABLE rows that
/// <see cref="NodeTypeDeclarationSelfTypingValidator"/> can only refuse going FORWARD: a NodeType
/// declaration persisted with <see cref="MeshNode.NodeType"/> naming ITSELF — enrolled in its own
/// instance query — is retyped to <see cref="MeshNode.NodeTypePath"/>, exactly the correction
/// #2245 applied to the static registrations.
///
/// <para><b>Why the write guard and the static retype were not enough (#2425 / #2506).</b> The
/// three built-in declarations (<c>User</c>, <c>VUser</c>, <c>Partition</c>) shipped self-typed
/// and were PERSISTED that way on live stores. #2245 fixed the static registrations and #2378
/// refuses any new write carrying the collision — but reads stay bad-data tolerant by design, so
/// a row already persisted with <c>nodeType: "User"</c> keeps answering every
/// <c>nodeType:User</c> query beside the real accounts, forever. On production that was ~600
/// <c>As&lt;User&gt; for User: value is NodeTypeDefinition</c> errors per hour from
/// <c>UserIdentityCache</c>, on an image that already contained both fixes: the defect had moved
/// from the CODE into the DATA, and only a repair of the data closes it.</para>
///
/// <para><b>Why this is a STORAGE-layer write, not <c>GetMeshNodeStream(path).Update</c>.</b> A
/// declaration path with a static claimant is served from the static registration
/// (<c>MeshDataSource.WithMeshNodes</c> seeds the per-node hub via <c>WithInitialData</c>,
/// bypassing persistence — the #2534 mechanism), so the durable row underneath is unreachable
/// through the serve path: un-readable and un-writable via the one application-level mutation
/// API. The row can only be corrected where it lives — the same seam
/// <c>LegacyUserPartitionRepair</c> already writes on. The adapter's change feed still fires, so
/// live <c>nodeType:</c> query subscriptions converge without a restart.</para>
///
/// <para><b>Scope.</b> Candidate paths are the statically-registered declarations
/// (<see cref="Mesh.Services.StaticNodeProviderExtensions.EnumerateStaticNodes"/> filtered to
/// <see cref="Configuration.NodeTypeDefinition"/> content) — the population that ever shipped
/// self-typed, plus any future built-in. A durable row at such a path is rewritten ONLY when it
/// matches <see cref="NodeTypeDeclarationSelfTypingValidator.IsSelfTypedDeclaration"/> — the
/// exact predicate the write guard refuses, shared so the two can never drift. Everything else —
/// already-correct declarations, real instances, package roots typed as an unrelated type — is
/// left untouched, so on a healthy store this pass reads one batch and writes nothing.</para>
///
/// <para><b>Safe every boot.</b> One <see cref="IStorageAdapter.ReadMany"/> batch (the same
/// multi-path probe the URL resolver runs constantly, so absent partitions answer "absent", never
/// fault), zero writes when nothing matches, and idempotent when something does: the healed row no
/// longer matches the predicate, and concurrent replicas retyping the same row write the same
/// value under the monotonic-version guard. Fire-and-forget and failure-tolerant like
/// <c>InstalledPackageRepairService</c> — a repair must never delay or fail startup; a skipped
/// pass heals on the next boot.</para>
/// </summary>
/// <param name="hub">Hub supplying the service provider and serializer options.</param>
public sealed class SelfTypedDeclarationDurableRepair(IMessageHub hub) : IHostedService
{
    private IDisposable? subscription;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger<SelfTypedDeclarationDurableRepair>();
        // A mesh without a storage adapter has no durable rows to heal.
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null)
            return Task.CompletedTask;

        var options = hub.JsonSerializerOptions;
        var declarationPaths = hub.ServiceProvider.EnumerateStaticNodes()
            .Where(n => n.Content is Configuration.NodeTypeDefinition)
            .Select(n => n.Path)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (declarationPaths.Length == 0)
            return Task.CompletedTask;

        // Subscribe-and-return per the IHostedService rule (AsynchronousCalls.md): the turn
        // returns immediately and the work belongs to the observable chain.
        subscription = storage.ReadMany(declarationPaths, options)
            .Where(NodeTypeDeclarationSelfTypingValidator.IsSelfTypedDeclaration)
            .SelectMany(fossil => storage
                .Write(fossil with
                {
                    NodeType = MeshNode.NodeTypePath,
                    Version = MeshNode.NextVersion(fossil.Version),
                    LastModified = DateTimeOffset.UtcNow,
                }, options)
                .Do(_ => logger?.LogInformation(
                    "[SelfTypedDeclarationRepair] retyped durable declaration row '{Path}' from "
                    + "nodeType '{Old}' to '{New}' — it was enrolled in its own instance query "
                    + "(#2425/#2506)",
                    fossil.Path, fossil.NodeType, MeshNode.NodeTypePath))
                // Per-row tolerance: one path that cannot be written (a read-only claimant, a
                // backend hiccup) must not stop the remaining rows from healing. Logged, and
                // retried on the next boot — the fossil still matches the predicate.
                .Catch<MeshNode?, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "[SelfTypedDeclarationRepair] retyping '{Path}' failed; it stays "
                        + "self-typed and will be retried on the next start", fossil.Path);
                    return Observable.Return<MeshNode?>(null);
                }))
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "[SelfTypedDeclarationRepair] declaration sweep failed; durable self-typed "
                    + "declaration rows (if any) stay unhealed until the next start"));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        subscription?.Dispose();
        subscription = null;
        return Task.CompletedTask;
    }
}
