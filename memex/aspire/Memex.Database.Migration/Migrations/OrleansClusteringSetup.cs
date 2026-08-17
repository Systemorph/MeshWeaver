using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Creates the Orleans cluster-membership AND grain-persistence tables in the dedicated
/// <c>orleans</c> database (same Postgres server, separate DB) so the portal silo can use
/// Postgres-backed AdoNet clustering instead of <c>Localhost</c>, and a Postgres-backed
/// <c>PubSubStore</c> for streaming pub-sub. The Aspire AppHost declares the <c>orleans</c>
/// database and injects its connection string (<c>ConnectionStrings:orleans</c>); this step
/// runs the official Orleans 10 PostgreSQL scripts (<c>Shared/PostgreSQL-Main.sql</c> +
/// <c>Orleans.Clustering.AdoNet/PostgreSQL-Clustering.sql</c> +
/// <c>Orleans.Persistence.AdoNet/PostgreSQL-Persistence.sql</c>) verbatim.
///
/// <para>🚨 <b>Why persistence tables exist here at all</b> (issue #1729): they back
/// <c>PubSubStore</c>, the grain storage holding each stream's subscriber list. With Orleans'
/// default in-memory store that list dies with whichever silo hosted the
/// <c>PubSubRendezvousGrain</c>, and every rolling deploy drops a silo — after which publishes to
/// that stream are DISCARDED while still reporting success, so cross-silo replies to
/// <c>portal/</c>, <c>mesh/</c>, <c>client/</c>, <c>cache/</c> hubs vanish with nothing logged.
/// A durable store is what makes the subscription outlive the silo that registered it.</para>
///
/// <para><b>Idempotent, per phase:</b> the scripts use plain <c>CREATE</c> (no
/// <c>IF NOT EXISTS</c>), so each phase gates on its own marker table — <c>orleansquery</c> for
/// membership, <c>orleansstorage</c> for persistence. They are checked SEPARATELY on purpose: an
/// existing deployment already has the membership tables, and a single combined gate would skip
/// the persistence phase forever on exactly the clusters that need it. The Orleans providers do
/// NOT auto-create these tables, so this must run before the silo starts.</para>
///
/// <para>Skipped when no <c>orleans</c> connection string is configured (e.g. an Azure-Tables /
/// Localhost deployment that doesn't use Postgres clustering).</para>
/// </summary>
public static class OrleansClusteringSetup
{
    public static async Task RunAsync(string orleansConnectionString, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(orleansConnectionString))
        {
            logger.LogInformation("[OrleansClustering] No 'orleans' connection string — skipping (non-AdoNet clustering).");
            return;
        }

        await EnsureDatabaseExistsAsync(orleansConnectionString, logger);

        await using var conn = new NpgsqlConnection(orleansConnectionString);
        await conn.OpenAsync();

        // Phase 1 — membership (OrleansQuery + the membership tables). Must run first: the
        // persistence script INSERTs its query texts INTO OrleansQuery.
        if (await TableExistsAsync(conn, "orleansquery"))
            logger.LogInformation("[OrleansClustering] Membership tables already present — nothing to do.");
        else
        {
            logger.LogInformation("[OrleansClustering] Creating Orleans membership tables in the 'orleans' database.");
            await using var cmd = new NpgsqlCommand(MembershipScript, conn);
            await cmd.ExecuteNonQueryAsync();
            logger.LogInformation("[OrleansClustering] Orleans membership tables created.");
        }

        // Phase 2 — grain persistence, which backs PubSubStore. TWO independent artefacts, gated
        // SEPARATELY, because the script produces two things and having one is not having the other.
        //
        // 🚨 This cost memex-cloud a failed rollout (#1798). The gate used to be "does the
        // orleansstorage TABLE exist" — but what AdoNetGrainStorage.Init actually reads is the four
        // QUERY ROWS in OrleansQuery, and it resolves them with .Single(), so a missing row is an
        // unhandled `Sequence contains no matching element` that kills silo startup outright. Prod
        // had the table from an earlier attempt and NOT the rows, so the table-gate reported
        // "already present — nothing to do" and skipped the INSERTs forever, on exactly the
        // deployments that needed them. A fresh database creates both together and can never
        // reproduce it — which is why the original local test passed and prod did not.
        //
        // Gate each artefact on ITS OWN evidence, and on the evidence the CONSUMER uses.
        var hasStorageTable = await TableExistsAsync(conn, "orleansstorage");
        var missingKeys = await MissingQueryKeysAsync(conn, PersistenceQueryKeys);

        if (hasStorageTable && missingKeys.Count == 0)
            logger.LogInformation("[OrleansClustering] Persistence table and all {Count} query keys present — nothing to do.",
                PersistenceQueryKeys.Length);

        if (!hasStorageTable)
        {
            logger.LogInformation("[OrleansClustering] Creating the Orleans persistence table (PubSubStore).");
            await using var cmd = new NpgsqlCommand(PersistenceTableScript, conn);
            await cmd.ExecuteNonQueryAsync();
            logger.LogInformation("[OrleansClustering] Orleans persistence table created.");
        }

        if (missingKeys.Count > 0)
        {
            // Delete-then-insert the whole set rather than only the missing ones: the texts are
            // Orleans' own, versioned with the package, so a row that exists but is STALE is just
            // as broken as one that is absent — and re-writing all four is what makes this
            // converge from any prior partial state.
            //
            // 🚨 ONE TRANSACTION, and the reason is the whole point of this file. The DELETE alone
            // produces exactly the state #1798 was: rows absent, AdoNetGrainStorage.Init .Single()s
            // for WriteToStorageKey, every silo crash-loops. So a repair that deleted and then
            // failed to reinsert would not merely leave the problem unfixed — it would MANUFACTURE
            // the outage on a database that was previously fine, and it is reachable in precisely
            // the population this method exists for (an existing deployment, mid-repair). Wrapped,
            // the failure mode is "nothing changed" instead of "the keys are gone"; the exception
            // then propagates and fails the migration loudly, which is the correct outcome.
            logger.LogInformation(
                "[OrleansClustering] Persistence query keys missing ({Missing}) — writing all {Count}.",
                string.Join(", ", missingKeys), PersistenceQueryKeys.Length);
            await using (var tx = await conn.BeginTransactionAsync())
            {
                await using (var del = new NpgsqlCommand(
                    "DELETE FROM OrleansQuery WHERE QueryKey = ANY(@keys)", conn, tx))
                {
                    del.Parameters.AddWithValue("keys", PersistenceQueryKeys);
                    await del.ExecuteNonQueryAsync();
                }
                await using (var cmd = new NpgsqlCommand(PersistenceQueriesScript, conn, tx))
                    await cmd.ExecuteNonQueryAsync();
                await tx.CommitAsync();
            }
            logger.LogInformation("[OrleansClustering] Orleans persistence query keys written.");
        }

        // 🚨 Assert on the SAME evidence the gate reads and the consumer uses — the query keys, not
        // the table. A transaction that rolled back leaves the database unchanged, which is the
        // safe outcome but NOT a provisioned one, and a migration that reported success there would
        // hand the silo a database it cannot start against. Fail loudly instead.
        var stillMissing = await MissingQueryKeysAsync(conn, PersistenceQueryKeys);
        if (stillMissing.Count > 0)
            throw new InvalidOperationException(
                $"Orleans persistence query keys are still missing after provisioning: "
                + $"{string.Join(", ", stillMissing)}. A silo configured with an AdoNet PubSubStore "
                + "cannot start without them (AdoNetGrainStorage.Init resolves them with .Single()).");
    }

    /// <summary>
    /// The four <c>OrleansQuery</c> keys <c>AdoNetGrainStorage</c> resolves with <c>.Single()</c> at
    /// silo start. These — not the table — are what a silo configured with an AdoNet
    /// <c>PubSubStore</c> fails to start without.
    /// </summary>
    private static readonly string[] PersistenceQueryKeys =
    [
        "WriteToStorageKey", "ReadFromStorageKey", "ClearStorageKey", "DeleteStorageKey"
    ];

    /// <summary>
    /// Which of <paramref name="required"/> are NOT present in <c>OrleansQuery</c>. Returns all of
    /// them when the table itself is absent, so the caller writes them once membership has created it.
    /// </summary>
    private static async Task<IReadOnlyList<string>> MissingQueryKeysAsync(
        NpgsqlConnection conn, string[] required)
    {
        if (!await TableExistsAsync(conn, "orleansquery"))
            return required;

        await using var cmd = new NpgsqlCommand(
            "SELECT QueryKey FROM OrleansQuery WHERE QueryKey = ANY(@keys)", conn);
        cmd.Parameters.AddWithValue("keys", required);
        var present = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                present.Add(reader.GetString(0));
        return required.Where(k => !present.Contains(k)).ToArray();
    }

    /// <summary>
    /// Whether <paramref name="tableName"/> exists in the schema the Orleans scripts would create it
    /// in — i.e. the FIRST writable schema on this connection's <c>search_path</c>, which is what an
    /// unqualified <c>CREATE TABLE</c> resolves to and therefore what <c>current_schema()</c>
    /// returns.
    ///
    /// <para>🚨 The schema predicate is load-bearing, not tidiness. This is the gate that decides
    /// whether a creation script runs, and <c>information_schema.tables</c> lists EVERY schema — so
    /// matching on <c>table_name</c> alone lets a same-named table anywhere in the database report
    /// "already present" and skip a script that in fact never ran. A gate that can pass on the wrong
    /// evidence is worse than no gate (AGENTS.md → "a verification step that cannot fail is not a
    /// verification step").</para>
    /// </summary>
    private static async Task<bool> TableExistsAsync(NpgsqlConnection conn, string tableName)
    {
        await using var check = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables "
            + "WHERE table_name = @t AND table_schema = current_schema())", conn);
        check.Parameters.AddWithValue("t", tableName);
        return (bool)(await check.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Creates the target database if it does not yet exist (self-managed Postgres — Compose/Helm
    /// pgvector container). Azure-managed Postgres pre-creates databases declared in the AppHost,
    /// so we skip the maintenance-connection path there (the app identity typically can't CREATE
    /// DATABASE on Flexible Server anyway).
    /// </summary>
    private static async Task EnsureDatabaseExistsAsync(string connectionString, ILogger logger)
    {
        if (connectionString.Contains("database.azure.com", StringComparison.OrdinalIgnoreCase))
            return;

        var targetDb = new NpgsqlConnectionStringBuilder(connectionString).Database ?? "orleans";
        var maintenanceCs = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString;

        await using var admin = new NpgsqlConnection(maintenanceCs);
        await admin.OpenAsync();
        await using var check = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @db", admin);
        check.Parameters.AddWithValue("db", targetDb);
        if (await check.ExecuteScalarAsync() is null)
        {
            logger.LogInformation("[OrleansClustering] Database '{Db}' does not exist — creating it.", targetDb);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{targetDb.Replace("\"", "\"\"")}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
    }

    // Verbatim Orleans 10 PostgreSQL clustering scripts: Shared/PostgreSQL-Main.sql (OrleansQuery)
    // followed by Orleans.Clustering.AdoNet/PostgreSQL-Clustering.sql (membership tables + queries).
    // Do not edit — keep in sync with the Microsoft.Orleans.Clustering.AdoNet package version.
    private const string MembershipScript = @"
CREATE TABLE OrleansQuery
(
    QueryKey varchar(64) NOT NULL,
    QueryText varchar(8000) NOT NULL,

    CONSTRAINT OrleansQuery_Key PRIMARY KEY(QueryKey)
);

-- For each deployment, there will be only one (active) membership version table version column which will be updated periodically.
CREATE TABLE OrleansMembershipVersionTable
(
    DeploymentId varchar(150) NOT NULL,
    Timestamp timestamptz(3) NOT NULL DEFAULT now(),
    Version integer NOT NULL DEFAULT 0,

    CONSTRAINT PK_OrleansMembershipVersionTable_DeploymentId PRIMARY KEY(DeploymentId)
);

-- Every silo instance has a row in the membership table.
CREATE TABLE OrleansMembershipTable
(
    DeploymentId varchar(150) NOT NULL,
    Address varchar(45) NOT NULL,
    Port integer NOT NULL,
    Generation integer NOT NULL,
    SiloName varchar(150) NOT NULL,
    HostName varchar(150) NOT NULL,
    Status integer NOT NULL,
    ProxyPort integer NULL,
    SuspectTimes varchar(8000) NULL,
    StartTime timestamptz(3) NOT NULL,
    IAmAliveTime timestamptz(3) NOT NULL,

    CONSTRAINT PK_MembershipTable_DeploymentId PRIMARY KEY(DeploymentId, Address, Port, Generation),
    CONSTRAINT FK_MembershipTable_MembershipVersionTable_DeploymentId FOREIGN KEY (DeploymentId) REFERENCES OrleansMembershipVersionTable (DeploymentId)
);

CREATE FUNCTION update_i_am_alive_time(
    deployment_id OrleansMembershipTable.DeploymentId%TYPE,
    address_arg OrleansMembershipTable.Address%TYPE,
    port_arg OrleansMembershipTable.Port%TYPE,
    generation_arg OrleansMembershipTable.Generation%TYPE,
    i_am_alive_time OrleansMembershipTable.IAmAliveTime%TYPE)
  RETURNS void AS
$func$
BEGIN
    -- This is expected to never fail by Orleans, so return value
    -- is not needed nor is it checked.
    UPDATE OrleansMembershipTable as d
    SET
        IAmAliveTime = i_am_alive_time
    WHERE
        d.DeploymentId = deployment_id AND deployment_id IS NOT NULL
        AND d.Address = address_arg AND address_arg IS NOT NULL
        AND d.Port = port_arg AND port_arg IS NOT NULL
        AND d.Generation = generation_arg AND generation_arg IS NOT NULL;
END
$func$ LANGUAGE plpgsql;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'UpdateIAmAlivetimeKey','
    -- This is expected to never fail by Orleans, so return value
    -- is not needed nor is it checked.
    SELECT * from update_i_am_alive_time(
        @DeploymentId,
        @Address,
        @Port,
        @Generation,
        @IAmAliveTime
    );
');

CREATE FUNCTION insert_membership_version(
    DeploymentIdArg OrleansMembershipTable.DeploymentId%TYPE
)
  RETURNS TABLE(row_count integer) AS
$func$
DECLARE
    RowCountVar int := 0;
BEGIN

    BEGIN

        INSERT INTO OrleansMembershipVersionTable
        (
            DeploymentId
        )
        SELECT DeploymentIdArg
        ON CONFLICT (DeploymentId) DO NOTHING;

        GET DIAGNOSTICS RowCountVar = ROW_COUNT;

        ASSERT RowCountVar <> 0, 'no rows affected, rollback';

        RETURN QUERY SELECT RowCountVar;
    EXCEPTION
    WHEN assert_failure THEN
        RETURN QUERY SELECT RowCountVar;
    END;

END
$func$ LANGUAGE plpgsql;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'InsertMembershipVersionKey','
    SELECT * FROM insert_membership_version(
        @DeploymentId
    );
');

CREATE FUNCTION insert_membership(
    DeploymentIdArg OrleansMembershipTable.DeploymentId%TYPE,
    AddressArg      OrleansMembershipTable.Address%TYPE,
    PortArg         OrleansMembershipTable.Port%TYPE,
    GenerationArg   OrleansMembershipTable.Generation%TYPE,
    SiloNameArg     OrleansMembershipTable.SiloName%TYPE,
    HostNameArg     OrleansMembershipTable.HostName%TYPE,
    StatusArg       OrleansMembershipTable.Status%TYPE,
    ProxyPortArg    OrleansMembershipTable.ProxyPort%TYPE,
    StartTimeArg    OrleansMembershipTable.StartTime%TYPE,
    IAmAliveTimeArg OrleansMembershipTable.IAmAliveTime%TYPE,
    VersionArg      OrleansMembershipVersionTable.Version%TYPE)
  RETURNS TABLE(row_count integer) AS
$func$
DECLARE
    RowCountVar int := 0;
BEGIN

    BEGIN
        INSERT INTO OrleansMembershipTable
        (
            DeploymentId,
            Address,
            Port,
            Generation,
            SiloName,
            HostName,
            Status,
            ProxyPort,
            StartTime,
            IAmAliveTime
        )
        SELECT
            DeploymentIdArg,
            AddressArg,
            PortArg,
            GenerationArg,
            SiloNameArg,
            HostNameArg,
            StatusArg,
            ProxyPortArg,
            StartTimeArg,
            IAmAliveTimeArg
        ON CONFLICT (DeploymentId, Address, Port, Generation) DO
            NOTHING;


        GET DIAGNOSTICS RowCountVar = ROW_COUNT;

        UPDATE OrleansMembershipVersionTable
        SET
            Timestamp = now(),
            Version = Version + 1
        WHERE
            DeploymentId = DeploymentIdArg AND DeploymentIdArg IS NOT NULL
            AND Version = VersionArg AND VersionArg IS NOT NULL
            AND RowCountVar > 0;

        GET DIAGNOSTICS RowCountVar = ROW_COUNT;

        ASSERT RowCountVar <> 0, 'no rows affected, rollback';


        RETURN QUERY SELECT RowCountVar;
    EXCEPTION
    WHEN assert_failure THEN
        RETURN QUERY SELECT RowCountVar;
    END;

END
$func$ LANGUAGE plpgsql;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'InsertMembershipKey','
    SELECT * FROM insert_membership(
        @DeploymentId,
        @Address,
        @Port,
        @Generation,
        @SiloName,
        @HostName,
        @Status,
        @ProxyPort,
        @StartTime,
        @IAmAliveTime,
        @Version
    );
');

CREATE FUNCTION update_membership(
    DeploymentIdArg OrleansMembershipTable.DeploymentId%TYPE,
    AddressArg      OrleansMembershipTable.Address%TYPE,
    PortArg         OrleansMembershipTable.Port%TYPE,
    GenerationArg   OrleansMembershipTable.Generation%TYPE,
    StatusArg       OrleansMembershipTable.Status%TYPE,
    SuspectTimesArg OrleansMembershipTable.SuspectTimes%TYPE,
    IAmAliveTimeArg OrleansMembershipTable.IAmAliveTime%TYPE,
    VersionArg      OrleansMembershipVersionTable.Version%TYPE
  )
  RETURNS TABLE(row_count integer) AS
$func$
DECLARE
    RowCountVar int := 0;
BEGIN

    BEGIN

    UPDATE OrleansMembershipVersionTable
    SET
        Timestamp = now(),
        Version = Version + 1
    WHERE
        DeploymentId = DeploymentIdArg AND DeploymentIdArg IS NOT NULL
        AND Version = VersionArg AND VersionArg IS NOT NULL;


    GET DIAGNOSTICS RowCountVar = ROW_COUNT;

    UPDATE OrleansMembershipTable
    SET
        Status = StatusArg,
        SuspectTimes = SuspectTimesArg,
        IAmAliveTime = IAmAliveTimeArg
    WHERE
        DeploymentId = DeploymentIdArg AND DeploymentIdArg IS NOT NULL
        AND Address = AddressArg AND AddressArg IS NOT NULL
        AND Port = PortArg AND PortArg IS NOT NULL
        AND Generation = GenerationArg AND GenerationArg IS NOT NULL
        AND RowCountVar > 0;


        GET DIAGNOSTICS RowCountVar = ROW_COUNT;

        ASSERT RowCountVar <> 0, 'no rows affected, rollback';


        RETURN QUERY SELECT RowCountVar;
    EXCEPTION
    WHEN assert_failure THEN
        RETURN QUERY SELECT RowCountVar;
    END;

END
$func$ LANGUAGE plpgsql;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'UpdateMembershipKey','
    SELECT * FROM update_membership(
        @DeploymentId,
        @Address,
        @Port,
        @Generation,
        @Status,
        @SuspectTimes,
        @IAmAliveTime,
        @Version
    );
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'MembershipReadRowKey','
    SELECT
        v.DeploymentId,
        m.Address,
        m.Port,
        m.Generation,
        m.SiloName,
        m.HostName,
        m.Status,
        m.ProxyPort,
        m.SuspectTimes,
        m.StartTime,
        m.IAmAliveTime,
        v.Version
    FROM
        OrleansMembershipVersionTable v
        -- This ensures the version table will returned even if there is no matching membership row.
        LEFT OUTER JOIN OrleansMembershipTable m ON v.DeploymentId = m.DeploymentId
        AND Address = @Address AND @Address IS NOT NULL
        AND Port = @Port AND @Port IS NOT NULL
        AND Generation = @Generation AND @Generation IS NOT NULL
    WHERE
        v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'MembershipReadAllKey','
    SELECT
        v.DeploymentId,
        m.Address,
        m.Port,
        m.Generation,
        m.SiloName,
        m.HostName,
        m.Status,
        m.ProxyPort,
        m.SuspectTimes,
        m.StartTime,
        m.IAmAliveTime,
        v.Version
    FROM
        OrleansMembershipVersionTable v LEFT OUTER JOIN OrleansMembershipTable m
        ON v.DeploymentId = m.DeploymentId
    WHERE
        v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'DeleteMembershipTableEntriesKey','
    DELETE FROM OrleansMembershipTable
    WHERE DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
    DELETE FROM OrleansMembershipVersionTable
    WHERE DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'GatewaysQueryKey','
    SELECT
        Address,
        ProxyPort,
        Generation
    FROM
        OrleansMembershipTable
    WHERE
        DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
        AND Status = @Status AND @Status IS NOT NULL
        AND ProxyPort > 0;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'CleanupDefunctSiloEntriesKey','
    DELETE FROM OrleansMembershipTable
    WHERE DeploymentId = @DeploymentId
        AND @DeploymentId IS NOT NULL
        AND IAmAliveTime < @IAmAliveTime
        AND Status != 3;
');
";

    // Verbatim Orleans 10 PostgreSQL persistence script:
    // Orleans.Persistence.AdoNet/PostgreSQL-Persistence.sql (OrleansStorage + its four queries).
    // Backs the PubSubStore grain storage — see the class remarks and issue #1729.
    // Do not edit — keep in sync with the Microsoft.Orleans.Persistence.AdoNet package version.
    private const string PersistenceTableScript = @"
CREATE TABLE OrleansStorage
(
    grainidhash integer NOT NULL,
    grainidn0 bigint NOT NULL,
    grainidn1 bigint NOT NULL,
    graintypehash integer NOT NULL,
    graintypestring character varying(512)  NOT NULL,
    grainidextensionstring character varying(512) ,
    serviceid character varying(150)  NOT NULL,
    payloadbinary bytea,
    modifiedon timestamp without time zone NOT NULL,
    version integer
);

CREATE INDEX ix_orleansstorage
    ON orleansstorage USING btree
    (grainidhash, graintypehash);

CREATE OR REPLACE FUNCTION writetostorage(
    _grainidhash integer,
    _grainidn0 bigint,
    _grainidn1 bigint,
    _graintypehash integer,
    _graintypestring character varying,
    _grainidextensionstring character varying,
    _serviceid character varying,
    _grainstateversion integer,
    _payloadbinary bytea)
    RETURNS TABLE(newgrainstateversion integer)
    LANGUAGE 'plpgsql'
AS $function$
    DECLARE
     _newGrainStateVersion integer := _GrainStateVersion;
     RowCountVar integer := 0;

    BEGIN

    -- Grain state is not null, so the state must have been read from the storage before.
    -- Let's try to update it.
    --
    -- When Orleans is running in normal, non-split state, there will
    -- be only one grain with the given ID and type combination only. This
    -- grain saves states mostly serially if Orleans guarantees are upheld. Even
    -- if not, the updates should work correctly due to version number.
    --
    -- In split brain situations there can be a situation where there are two or more
    -- grains with the given ID and type combination. When they try to INSERT
    -- concurrently, the table needs to be locked pessimistically before one of
    -- the grains gets @GrainStateVersion = 1 in return and the other grains will fail
    -- to update storage. The following arrangement is made to reduce locking in normal operation.
    --
    -- If the version number explicitly returned is still the same, Orleans interprets it so the update did not succeed
    -- and throws an InconsistentStateException.
    --
    -- See further information at https://learn.microsoft.com/dotnet/orleans/grains/grain-persistence.
    IF _GrainStateVersion IS NOT NULL
    THEN
        UPDATE OrleansStorage
        SET
            PayloadBinary = _PayloadBinary,
            ModifiedOn = (now() at time zone 'utc'),
            Version = Version + 1

        WHERE
            GrainIdHash = _GrainIdHash AND _GrainIdHash IS NOT NULL
            AND GrainTypeHash = _GrainTypeHash AND _GrainTypeHash IS NOT NULL
            AND GrainIdN0 = _GrainIdN0 AND _GrainIdN0 IS NOT NULL
            AND GrainIdN1 = _GrainIdN1 AND _GrainIdN1 IS NOT NULL
            AND GrainTypeString = _GrainTypeString AND _GrainTypeString IS NOT NULL
            AND ((_GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString = _GrainIdExtensionString) OR _GrainIdExtensionString IS NULL AND GrainIdExtensionString IS NULL)
            AND ServiceId = _ServiceId AND _ServiceId IS NOT NULL
            AND Version IS NOT NULL AND Version = _GrainStateVersion AND _GrainStateVersion IS NOT NULL;

        GET DIAGNOSTICS RowCountVar = ROW_COUNT;
        IF RowCountVar > 0
        THEN
            _newGrainStateVersion := _GrainStateVersion + 1;
        END IF;
    END IF;

    -- The grain state has not been read. The following locks rather pessimistically
    -- to ensure only one INSERT succeeds.
    IF _GrainStateVersion IS NULL
    THEN
        INSERT INTO OrleansStorage
        (
            GrainIdHash,
            GrainIdN0,
            GrainIdN1,
            GrainTypeHash,
            GrainTypeString,
            GrainIdExtensionString,
            ServiceId,
            PayloadBinary,
            ModifiedOn,
            Version
        )
        SELECT
            _GrainIdHash,
            _GrainIdN0,
            _GrainIdN1,
            _GrainTypeHash,
            _GrainTypeString,
            _GrainIdExtensionString,
            _ServiceId,
            _PayloadBinary,
           (now() at time zone 'utc'),
            1
        WHERE NOT EXISTS
         (
            -- There should not be any version of this grain state.
            SELECT 1
            FROM OrleansStorage
            WHERE
                GrainIdHash = _GrainIdHash AND _GrainIdHash IS NOT NULL
                AND GrainTypeHash = _GrainTypeHash AND _GrainTypeHash IS NOT NULL
                AND GrainIdN0 = _GrainIdN0 AND _GrainIdN0 IS NOT NULL
                AND GrainIdN1 = _GrainIdN1 AND _GrainIdN1 IS NOT NULL
                AND GrainTypeString = _GrainTypeString AND _GrainTypeString IS NOT NULL
                AND ((_GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString = _GrainIdExtensionString) OR _GrainIdExtensionString IS NULL AND GrainIdExtensionString IS NULL)
                AND ServiceId = _ServiceId AND _ServiceId IS NOT NULL
         );

        GET DIAGNOSTICS RowCountVar = ROW_COUNT;
        IF RowCountVar > 0
        THEN
            _newGrainStateVersion := 1;
        END IF;
    END IF;

    RETURN QUERY SELECT _newGrainStateVersion AS NewGrainStateVersion;
END

$function$;
";

    // The four OrleansQuery rows AdoNetGrainStorage.Init resolves with .Single(). Split from
    // the table DDL above so each can be gated on its own evidence (#1798).
    private const string PersistenceQueriesScript = @"INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'WriteToStorageKey','

        select * from WriteToStorage(@GrainIdHash, @GrainIdN0, @GrainIdN1, @GrainTypeHash, @GrainTypeString, @GrainIdExtensionString, @ServiceId, @GrainStateVersion, @PayloadBinary);
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'ReadFromStorageKey','
    SELECT
        PayloadBinary,
        (now() at time zone ''utc''),
        Version
    FROM
        OrleansStorage
    WHERE
        GrainIdHash = @GrainIdHash
        AND GrainTypeHash = @GrainTypeHash AND @GrainTypeHash IS NOT NULL
        AND GrainIdN0 = @GrainIdN0 AND @GrainIdN0 IS NOT NULL
        AND GrainIdN1 = @GrainIdN1 AND @GrainIdN1 IS NOT NULL
        AND GrainTypeString = @GrainTypeString AND GrainTypeString IS NOT NULL
        AND ((@GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString = @GrainIdExtensionString) OR @GrainIdExtensionString IS NULL AND GrainIdExtensionString IS NULL)
        AND ServiceId = @ServiceId AND @ServiceId IS NOT NULL
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'ClearStorageKey','
    UPDATE OrleansStorage
    SET
        PayloadBinary = NULL,
        Version = Version + 1
    WHERE
        GrainIdHash = @GrainIdHash AND @GrainIdHash IS NOT NULL
        AND GrainTypeHash = @GrainTypeHash AND @GrainTypeHash IS NOT NULL
        AND GrainIdN0 = @GrainIdN0 AND @GrainIdN0 IS NOT NULL
        AND GrainIdN1 = @GrainIdN1 AND @GrainIdN1 IS NOT NULL
        AND GrainTypeString = @GrainTypeString AND @GrainTypeString IS NOT NULL
        AND ((@GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString = @GrainIdExtensionString) OR @GrainIdExtensionString IS NULL AND GrainIdExtensionString IS NULL)
        AND ServiceId = @ServiceId AND @ServiceId IS NOT NULL
        AND Version IS NOT NULL AND Version = @GrainStateVersion AND @GrainStateVersion IS NOT NULL
    Returning Version as NewGrainStateVersion
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'DeleteStorageKey','
    DELETE FROM OrleansStorage
    WHERE
        GrainIdHash = @GrainIdHash AND @GrainIdHash IS NOT NULL
        AND GrainTypeHash = @GrainTypeHash AND @GrainTypeHash IS NOT NULL
        AND GrainIdN0 = @GrainIdN0 AND @GrainIdN0 IS NOT NULL
        AND GrainIdN1 = @GrainIdN1 AND @GrainIdN1 IS NOT NULL
        AND GrainTypeString = @GrainTypeString AND @GrainTypeString IS NOT NULL
        AND ((@GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString = @GrainIdExtensionString) OR @GrainIdExtensionString IS NULL AND GrainIdExtensionString IS NULL)
        AND ServiceId = @ServiceId AND @ServiceId IS NOT NULL
        AND Version IS NOT NULL AND Version = @GrainStateVersion AND @GrainStateVersion IS NOT NULL
    Returning Version + 1 as NewGrainStateVersion
');
";
}
