using MeshWeaver.Hosting.PostgreSql;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Shared state passed to every migration. <see cref="IsFreshDb"/> is set by the runner
/// after schema initialization — fresh DBs already have the latest schema and skip all
/// data repairs.
/// </summary>
public sealed record MigrationContext(
    NpgsqlDataSource DataSource,
    string ConnectionString,
    PostgreSqlStorageOptions Options,
    ILogger Logger,
    bool IsFreshDb);
