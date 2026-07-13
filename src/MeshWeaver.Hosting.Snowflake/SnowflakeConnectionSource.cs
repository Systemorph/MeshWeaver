using System.Data;
using System.Data.Common;
using Snowflake.Data.Client;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// Identifier quoting for Snowflake. Snowflake uppercases unquoted identifiers, while the
/// mesh's path router produces <b>lowercased</b> schema names (first path segment). Every
/// identifier in every DDL/DML statement therefore goes through <see cref="Quote"/> so
/// schema/table names match the PostgreSQL backend byte-for-byte.
/// </summary>
public static class SnowflakeIdentifiers
{
    /// <summary>Double-quotes an identifier, escaping embedded quotes.</summary>
    public static string Quote(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>Quotes a two-part <c>"schema"."table"</c> reference.</summary>
    public static string Qualify(string schema, string table)
        => Quote(schema) + "." + Quote(table);
}

/// <summary>
/// The one place that opens Snowflake connections and binds parameters. Wraps the
/// Snowflake.Data driver's built-in per-connection-string session pool (the role
/// <c>NpgsqlDataSource</c> plays for the PG backend). Registered as a DI-owned singleton
/// so disposal clears the driver pool with the mesh.
/// </summary>
public sealed class SnowflakeConnectionSource(string connectionString) : IDisposable
{
    /// <summary>The connection string this source opens against (host override points at the LocalStack emulator).</summary>
    public string ConnectionString { get; } = connectionString;

    /// <summary>
    /// Opens a pooled connection. Always an I/O leaf — call only from inside an
    /// <c>IIoPool</c> invoke, never on a hub scheduler.
    /// </summary>
    public async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SnowflakeDbConnection { ConnectionString = ConnectionString };
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Adds a named parameter to <paramref name="command"/>. This helper seals the driver's
    /// binding convention in ONE place: SQL uses <c>:name</c> placeholders and the parameter
    /// is registered under the bare name. <c>null</c> binds as <see cref="DBNull"/>.
    /// </summary>
    public static DbParameter AddParam(DbCommand command, string name, object? value, DbType dbType)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = dbType;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return parameter;
    }

    /// <summary>
    /// Clears the driver's session pool for THIS connection string so test fixtures /
    /// mesh disposal don't leak HTTPS sessions — scoped per pool so disposing one source
    /// can't disrupt another endpoint's sessions in the same process.
    /// </summary>
    public void Dispose()
    {
        try
        {
            SnowflakeDbConnectionPool.GetPool(ConnectionString).ClearPool();
        }
        catch (Exception)
        {
            // Pool teardown on process exit is best-effort; a failed clear leaks
            // nothing beyond already-closed sessions.
        }
    }
}
