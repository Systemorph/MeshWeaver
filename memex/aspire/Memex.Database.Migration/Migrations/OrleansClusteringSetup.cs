using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Creates the Orleans cluster-membership tables in the dedicated <c>orleans</c> database
/// (same Postgres server, separate DB) so the portal silo can use Postgres-backed AdoNet
/// clustering instead of <c>Localhost</c>. The Aspire AppHost declares the <c>orleans</c>
/// database and injects its connection string (<c>ConnectionStrings:orleans</c>); this step
/// runs the official Orleans 10 PostgreSQL membership scripts (<c>Shared/PostgreSQL-Main.sql</c>
/// + <c>Orleans.Clustering.AdoNet/PostgreSQL-Clustering.sql</c>) verbatim.
///
/// <para><b>Idempotent:</b> the scripts use plain <c>CREATE</c> (no <c>IF NOT EXISTS</c>), so we
/// gate on whether the <c>orleansquery</c> table already exists and only run them once. The
/// Orleans provider does NOT auto-create these tables, so this is required before the silo starts.</para>
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

        await using (var check = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'orleansquery')", conn))
        {
            if ((bool)(await check.ExecuteScalarAsync())!)
            {
                logger.LogInformation("[OrleansClustering] Membership tables already present — nothing to do.");
                return;
            }
        }

        logger.LogInformation("[OrleansClustering] Creating Orleans membership tables in the 'orleans' database.");
        await using (var cmd = new NpgsqlCommand(MembershipScript, conn))
            await cmd.ExecuteNonQueryAsync();
        logger.LogInformation("[OrleansClustering] Orleans membership tables created.");
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
}
