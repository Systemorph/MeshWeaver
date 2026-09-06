using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// 🚨 <b>Boot gate: a mesh with partition storage MUST be able to tear a partition down.</b>
/// Refuses to start when no <see cref="INodePostDeletionHandler"/> matches an ARBITRARY partition
/// root — the exact regression that produced MeshWeaver#3436.
///
/// <para><b>Why the probe carries a nonsense NodeType.</b> The defect was not "a handler is
/// missing"; it was "the handlers that exist are keyed on a hand-maintained pair of NodeType
/// strings (<c>Space</c>, <c>User</c>), and a partition root of any other type falls through".
/// A gate that probed with <c>Space</c> would have been GREEN throughout the incident, having
/// checked nothing that mattered — a gate that cannot fail in the case it exists for. So the
/// probe node is a partition root whose NodeType is a name no registration can possibly have
/// enumerated, which is precisely the shape of the real victims: <c>Store/Plugin</c>, a NodeType
/// declared in mesh CONTENT and therefore invisible to every <c>src/</c>-side list, including one
/// installed long after boot.</para>
///
/// <para><b>Why a startup REFUSAL and not a warning.</b> The failure is silent by construction:
/// the recursive delete removes the nodes and returns success, and only the Postgres schema —
/// with every satellite table under it — is left behind, invisible to a Space listing and, once a
/// later write bootstraps a fresh root and policy over it, un-deletable through the ordinary API.
/// Nobody reads a warning about a delete that reported Ok. A portal that cannot tear a partition
/// down is a portal that leaks a database on every space deletion, so it does not start.</para>
///
/// <para>🚨 <b>It has no arming condition, deliberately.</b> An earlier draft only armed when a
/// WRITABLE <see cref="IPartitionStorageProvider"/> was registered — and measurably went silent on
/// the Monolith test host, whose providers are all read-only, so a deliberately broken registration
/// booted clean. That is the "a gate never tests its own inputs" rule in runtime form: an
/// input-shaped condition turns "could not have run" into "passed". The gate is registered by
/// <c>AddGraph</c>, and calling <c>AddGraph</c> IS the statement that this mesh has partitions — so
/// once it is registered it always decides. Coverage is a pure property of the handler set, which
/// is always resolvable.</para>
///
/// <para>The complementary detector that no host can skip lives in the delete pipeline itself:
/// deleting a partition root with no matching handler logs <c>Critical</c> from
/// <c>MeshExtensions.ResolvePostDeletionHandlers</c>, on every host, whether or not the gate was
/// ever registered.</para>
/// </summary>
public sealed class PartitionTeardownCoverageGate : IHostedService
{
    // 🚨 RELEASED on StartAsync. A hosted-service instance outlives the mesh it belongs to (test
    // harnesses keep the started instances to stop them at teardown), so a retained field that
    // reaches the hub roots the entire DISPOSED hub graph across meshes —
    // MeshHubDisposalLeakTest names exactly that chain. This gate reads the service provider
    // once and needs nothing afterwards.
    private IMessageHub? hub;

    /// <summary>Captures the hub — held only until <see cref="StartAsync"/> consumes it.</summary>
    /// <param name="hub">Hub whose service provider carries the providers and handlers.</param>
    public PartitionTeardownCoverageGate(IMessageHub hub) => this.hub = hub;

    /// <summary>
    /// The NodeType of the probe node. Deliberately not a real type: the gate asserts that
    /// teardown coverage does NOT depend on knowing the type.
    /// </summary>
    internal const string ProbeNodeType = "PartitionTeardownProbe/UnknownInMeshType";

    /// <summary>The partition id of the probe node — never read or written, only classified.</summary>
    internal const string ProbePartition = "PartitionTeardownProbe";

    /// <summary>The synthetic partition root every mesh's handler set must cover.</summary>
    internal static MeshNode Probe { get; } =
        new(ProbePartition) { NodeType = ProbeNodeType, Name = ProbePartition };

    /// <summary>
    /// The gate's verdict, as a pure function of the handler set — exposed so a test can drive
    /// both outcomes without standing up a host.
    /// </summary>
    /// <param name="handlers">The registered post-deletion handlers.</param>
    /// <returns><c>null</c> when covered; otherwise the refusal message.</returns>
    internal static string? Verdict(IReadOnlyCollection<INodePostDeletionHandler> handlers)
    {
        if (handlers.Any(h => h.Matches(Probe)))
            return null;
        return
            "No INodePostDeletionHandler covers a partition ROOT, so deleting one would remove its "
            + "nodes and ORPHAN its backing store (the Postgres schema and every satellite table "
            + "under it) — invisible to every Space listing, and un-deletable through the ordinary "
            + "API once a later write bootstraps a fresh root over it (MeshWeaver#3436). The probe "
            + $"is a partition root typed '{ProbeNodeType}': coverage must be STRUCTURAL "
            + "(PartitionDefinition.IsPartitionRoot), because a partition root can carry a NodeType "
            + "declared in mesh content — Store/Plugin — that no src/-side registration can "
            + "enumerate. Registered handler NodeTypes: "
            + (handlers.Count == 0 ? "(none)" : string.Join(", ", handlers.Select(h => h.NodeType)))
            + ". Call AddGraph(), which registers PartitionDropPostDeletionHandler once.";
    }

    /// <summary>
    /// The REFUSAL itself: logs <c>Critical</c> and throws when <see cref="Verdict"/> objects.
    /// Separated from <see cref="StartAsync"/> — which is only "resolve the handlers and call
    /// this" — so a test drives the production refusal path rather than a re-statement of it.
    /// </summary>
    /// <param name="handlers">The registered post-deletion handlers.</param>
    /// <param name="logger">Diagnostics; the refusal is logged before it is thrown.</param>
    internal static void AssertCovered(
        IReadOnlyCollection<INodePostDeletionHandler> handlers, ILogger? logger)
    {
        var verdict = Verdict(handlers);
        if (verdict is null)
            return;
        logger?.LogCritical("[PartitionTeardown] {Message}", verdict);
        throw new InvalidOperationException(verdict);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Take the hub ONCE and drop the field — see the field comment.
        var meshHub = Interlocked.Exchange(ref hub, null);
        if (meshHub is null)
            return Task.CompletedTask;

        AssertCovered(
            meshHub.ServiceProvider.GetServices<INodePostDeletionHandler>().ToList(),
            meshHub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger<PartitionTeardownCoverageGate>());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
