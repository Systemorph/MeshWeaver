using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// Refuses to start a silo that was configured to run as one of SEVERAL silos (it supplied a
/// durable <see cref="StreamProviders.PubSubStore"/>) when its persistence does not declare
/// <see cref="CrossProcessChangeDelivery"/> — issue #4802, slice 3.
///
/// <para>🚨 Why a refusal and not a warning: since the Orleans memory-stream change feed was retired,
/// the storage layer's own feed (PostgreSQL <c>LISTEN/NOTIFY</c> → <see cref="IStorageAdapter.Changes"/>)
/// is the ONLY carrier of one process's commit to every other process. A second silo over a store
/// with no such feed (in-memory, file system) does not fail: it serves its pre-commit caches for the
/// life of the process, with no error and nothing to grep. A host that cannot be correct must not
/// come up looking healthy, so the silo refuses at start and says what to declare.</para>
///
/// <para>Registered by <see cref="OrleansServerRegistryExtensions.ConfigureMeshWeaverServer"/> ONLY
/// when a durable PubSubStore is supplied — the existing marker of a host that can run more than one
/// silo. A single-process host (the Monolith, a localhost silo, the bake silo, an in-process
/// <c>TestCluster</c>) needs no declaration and never meets this check.</para>
///
/// <para>Orleans runs every <see cref="IConfigurationValidator"/> while it constructs the silo's
/// hosted service, i.e. before the silo joins the cluster, so a refused silo never takes traffic.</para>
/// </summary>
internal sealed class CrossProcessChangeDeliveryValidator(IServiceProvider services) : IConfigurationValidator
{
    /// <summary>The refusal's message — public to the test assembly so the test pins the wording
    /// an operator will read rather than a substring of it.</summary>
    internal const string RefusalMessage =
        "This silo was configured for a multi-silo cluster (a durable Orleans PubSubStore was supplied), "
        + "but its persistence does not declare CrossProcessChangeDelivery. Without a storage change feed "
        + "that reaches every process, a commit made on one silo is never heard on the others, and they "
        + "serve stale caches with no error. Multi-silo MeshWeaver runs on PostgreSQL only: register "
        + "AddPartitionedPostgreSqlPersistence (it declares \"PostgreSQL LISTEN/NOTIFY\"), or, for a "
        + "backend whose feed genuinely reaches every process, call "
        + "services.DeclareCrossProcessChangeDelivery(\"<source>\"). A single-process host must not "
        + "supply a durable PubSubStore. See Doc/Architecture/DurableStreamsViaMeshNodes, "
        + "\"Multi-silo is PostgreSQL-only\" (#4802).";

    /// <inheritdoc />
    public void ValidateConfiguration()
    {
        // GetService, deliberately: ABSENCE is the condition under test, not a wiring mistake.
        if (services.GetService<CrossProcessChangeDelivery>() is null)
            throw new OrleansConfigurationException(RefusalMessage);
    }
}
