using System.Collections.Immutable;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memex.Portal.Distributed;

/// <summary>
/// Hard gate that runs once at portal startup when the silo is configured for AdoNet clustering:
/// asserts that the <c>orleans</c> database actually carries the <c>OrleansQuery</c> rows the
/// Orleans AdoNet providers read, and refuses to start the host if any are missing.
///
/// <para><b>Why this exists (#1798).</b> The portal already throws when
/// <c>Features:Orleans:Clustering=AdoNet</c> and <c>ConnectionStrings:orleans</c> is unset — but
/// that only proves the portal was TOLD where the database is. It says nothing about whether
/// anything ever provisioned it. In the incident, the connection string was present on the
/// PORTAL and absent from the MIGRATION's secret, so <c>OrleansClusteringSetup</c> logged
/// "skipping" at Information and created nothing. The portal then started, and
/// <c>AdoNetGrainStorage.Init</c> — which loads each of its query texts with
/// <see cref="System.Linq.Enumerable.Single{T}(System.Collections.Generic.IEnumerable{T})"/> —
/// threw <c>Sequence contains no elements</c> into a crash loop. That exception names no table,
/// no key, no connection string and no container: it is the least actionable possible rendering
/// of "the database was never provisioned", and prod was rolled back on it.</para>
///
/// <para>This gate is the consumer half of the pipeline rule that every step asserts its
/// predecessor's signal: <c>OrleansClusteringSetup</c> (Memex.Database.Migration) emits a signal
/// specific to what it provisioned (counted query keys, not "completed"), and this gate
/// independently re-checks the same contract against the database the silo will actually
/// use. Two checks of one contract — one at produce time, one at consume time — so a deployment
/// where the two components disagree about whether Orleans is in play fails LOUDLY at startup
/// naming exactly what to provision, instead of crash-looping on a LINQ exception.</para>
///
/// <para>Modelled on <see cref="DbVersionGate"/>, including its two hard-won behaviours: fail
/// CLOSED on anything the database says, and RETHROW a cancellation of the host's own startup
/// token (a rollout replacing the pod moments after it started is not a provisioning failure —
/// issue #1183).</para>
/// </summary>
public sealed class OrleansProvisioningGate(
    string orleansConnectionString,
    bool requiresGrainStorage,
    IHostApplicationLifetime lifetime,
    ILogger<OrleansProvisioningGate> logger) : IHostedService
{
    /// <summary>
    /// The nine <c>OrleansQuery</c> keys <c>AdoNetClusteringTable</c> loads at silo start. Taken
    /// from the verbatim Orleans PostgreSQL clustering script the migration runs — keep the two
    /// in step when the Orleans package version moves.
    /// </summary>
    public static readonly ImmutableArray<string> MembershipQueryKeys =
    [
        "UpdateIAmAlivetimeKey",
        "InsertMembershipVersionKey",
        "InsertMembershipKey",
        "UpdateMembershipKey",
        "MembershipReadRowKey",
        "MembershipReadAllKey",
        "DeleteMembershipTableEntriesKey",
        "GatewaysQueryKey",
        "CleanupDefunctSiloEntriesKey",
    ];

    /// <summary>
    /// The four <c>OrleansQuery</c> keys <c>AdoNetGrainStorage.Init</c> loads — each with a
    /// <c>.Single()</c>, which is why a missing row surfaces as <c>Sequence contains no
    /// elements</c> rather than as anything that names the database. These back
    /// <c>PubSubStore</c>; see the streaming-durability rationale on
    /// <c>OrleansClusteringSetup</c> (Memex.Database.Migration).
    /// </summary>
    public static readonly ImmutableArray<string> GrainStorageQueryKeys =
    [
        "WriteToStorageKey",
        "ReadFromStorageKey",
        "ClearStorageKey",
        "DeleteStorageKey",
    ];

    /// <summary>
    /// The keys this deployment requires: always membership (clustering is on whenever this gate
    /// is registered), plus grain storage when the silo also configures an AdoNet
    /// <c>PubSubStore</c>. Both derive from the single <c>useAdoNetClustering</c> decision in
    /// <c>Program.cs</c>, so the gate can never require more than the silo configures.
    /// </summary>
    public static ImmutableArray<string> RequiredKeys(bool requiresGrainStorage)
        => requiresGrainStorage
            ? [.. MembershipQueryKeys, .. GrainStorageQueryKeys]
            : MembershipQueryKeys;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var required = RequiredKeys(requiresGrainStorage);
        try
        {
            await using var conn = new NpgsqlConnection(orleansConnectionString);
            await conn.OpenAsync(cancellationToken);

            await using var cmd = new NpgsqlCommand(
                "SELECT querykey FROM orleansquery WHERE querykey = ANY(@keys)", conn);
            cmd.Parameters.AddWithValue("keys", required.ToArray());

            var present = new HashSet<string>(StringComparer.Ordinal);
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    present.Add(reader.GetString(0));
            }

            var missing = required.Where(k => !present.Contains(k)).ToArray();
            if (missing.Length > 0)
            {
                logger.LogCritical(
                    "Orleans AdoNet storage is configured but the 'orleans' database is not "
                    + "provisioned: OrleansQuery is missing {MissingCount} of {RequiredCount} "
                    + "required query keys ({Missing}). The db-migration container did not run "
                    + "OrleansClusteringSetup — almost always because ConnectionStrings__orleans "
                    + "is set on the PORTAL but not on the MIGRATION, where its absence is a "
                    + "skip. Provision it on the migration and re-run it. Refusing to start the "
                    + "portal (without this the silo crash-loops on 'Sequence contains no "
                    + "elements' from AdoNetGrainStorage.Init, which names none of the above).",
                    missing.Length, required.Length, string.Join(", ", missing));
                lifetime.StopApplication();
                return;
            }

            logger.LogInformation(
                "Orleans provisioning check passed: all {Count} required OrleansQuery keys are "
                + "present (membership{Storage}).",
                required.Length, requiresGrainStorage ? " + grain storage" : "");
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // The OrleansQuery table itself is absent — the migration's Orleans phase never ran
            // at all. This is the #1798 shape exactly.
            logger.LogCritical(ex,
                "Orleans AdoNet storage is configured but the 'orleans' database has no "
                + "OrleansQuery table — OrleansClusteringSetup never ran. Set "
                + "ConnectionStrings__orleans on the db-migration container and re-run it. "
                + "Refusing to start the portal.");
            lifetime.StopApplication();
        }
        catch (PostgresException ex) when (ex.SqlState == "3D000")
        {
            // The `orleans` DATABASE does not exist. Distinct from the table case: on managed
            // Postgres the migration deliberately does not CREATE DATABASE, so this points at
            // provisioning outside the migration rather than at the migration having skipped.
            logger.LogCritical(ex,
                "Orleans AdoNet storage is configured but the database named in "
                + "ConnectionStrings:orleans does not exist. On managed Postgres the migration "
                + "does not create it — declare it in the AppHost / provision it on the server. "
                + "Refusing to start the portal.");
            lifetime.StopApplication();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 🚨 Host startup was aborted (shutdown raced startup) — that is not a provisioning
            // verdict. Same lesson as DbVersionGate / issue #1183: honour the IHostedService
            // cancellation contract instead of misreporting an ordinary shutdown as a critical
            // failure to provision.
            //
            // …but SAY so on the way out (#1897). Rethrowing SILENTLY left the framework's
            // `Hosting failed to start` — Error, with no frame above the Npgsql cancel that knows
            // why — as the only record, and that reads exactly like a real provisioning failure:
            // the incident was filed at "medium confidence — equally plausible a race at shutdown
            // (expected) or a real timeout (a defect)". This gate is the only thing in the process
            // that can tell those apart. A check that did not run must not look like one that
            // passed; it must not look like one that FAILED either.
            logger.LogWarning(
                "Orleans provisioning check did NOT run: host startup was cancelled before the "
                + "query completed — shutdown raced startup (a rollout replacing this pod while it "
                + "was still starting). This is not a provisioning verdict: the orleans database "
                + "was neither confirmed nor faulted. Propagating the cancellation per the "
                + "IHostedService contract, so the 'Hosting failed to start' that follows is the "
                + "aborted startup and not a fault.");
            throw;
        }
        catch (Exception ex)
        {
            // Any other connection / auth error — fail CLOSED, like DbVersionGate. A silo that
            // cannot verify its clustering store must not join a cluster.
            logger.LogCritical(ex,
                "Orleans provisioning check failed unexpectedly against the 'orleans' database. "
                + "Refusing to start the portal.");
            lifetime.StopApplication();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
