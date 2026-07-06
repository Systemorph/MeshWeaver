using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Creates the <c>events</c> schema backing the app-level event-log outbox
/// (<c>PostgreSqlEventLogStore</c>): an append-only <c>events.event_log</c> of every mesh change and a
/// per-consumer <c>events.action_cursor</c>. This is the durable, replayable event queue the
/// scheduled-action / event-driven layer consumes.
///
/// <para><b>Idempotent</b>: all <c>CREATE … IF NOT EXISTS</c>. The <c>UNIQUE (path, kind, version)</c>
/// makes <c>Append</c> idempotent (a replayed event is not re-logged), and <c>seq BIGSERIAL</c> is the
/// monotonic cursor. No trigger on <c>mesh_nodes</c> — the log is written by the app-level writer
/// subscribing to the change feed, so this migration touches no write path.</para>
/// </summary>
public sealed class V40_CreateEventLogSchema : IMigration
{
    public int Version => 40;
    public string Description => "Create events schema (event_log + action_cursor) for the app-level event-log outbox";

    public async Task RunAsync(MigrationContext ctx)
    {
        await using var cmd = ctx.DataSource.CreateCommand("""
            CREATE SCHEMA IF NOT EXISTS events;

            CREATE TABLE IF NOT EXISTS events.event_log (
                seq         BIGSERIAL PRIMARY KEY,
                occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                namespace   TEXT NOT NULL DEFAULT '',
                path        TEXT NOT NULL,
                node_type   TEXT,
                kind        TEXT NOT NULL,
                version     BIGINT NOT NULL DEFAULT 0,
                payload     JSONB,
                CONSTRAINT uq_event_log_path_kind_version UNIQUE (path, kind, version)
            );

            CREATE TABLE IF NOT EXISTS events.action_cursor (
                consumer_id TEXT PRIMARY KEY,
                last_seq    BIGINT NOT NULL DEFAULT 0
            );
            """);
        await cmd.ExecuteNonQueryAsync();

        ctx.Logger.LogInformation("v40: created events schema (event_log + action_cursor)");
    }
}
