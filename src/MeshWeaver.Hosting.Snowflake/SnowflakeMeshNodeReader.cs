using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// THE one row-materialization helper for the Snowflake backend: turns a result-set row into a
/// <see cref="MeshNode"/>. The storage adapter, the cross-schema query provider and the version
/// query all call this — deliberately fixing the PG backend's three drifted copies
/// (<c>PostgreSqlStorageAdapter.ReadMeshNode</c>, <c>PostgreSqlCrossSchemaQueryProvider.ReadMeshNode</c>,
/// <c>PostgreSqlVersionQuery.ReadMeshNode</c>) by unifying their behaviors:
/// <list type="bullet">
///   <item><b>Name-based field lookup</b> — no positional assumptions, and optional columns
///     (<c>description</c>, <c>sync_behavior</c>) may simply be absent from the SELECT, so the
///     same reader handles <c>mesh_nodes</c>-shaped rows, satellite rows and history rows.</item>
///   <item><b><c>$type</c> re-fronting</b> — Snowflake VARIANT alphabetizes object keys exactly
///     like PG jsonb, which breaks System.Text.Json polymorphic deserialization (the
///     discriminator must be the FIRST property); <see cref="EnsureTypeDiscriminatorFirst"/>
///     recursively restores it (the adapter copy had this, the cross-schema copy did not — a
///     latent PG bug not reproduced here).</item>
///   <item><b>Poisoned-content tolerance</b> — a malformed content payload degrades THAT row's
///     <c>Content</c> to <c>null</c> instead of faulting the whole query (from the cross-schema
///     copy; production repro: one Thread row with a misplaced <c>$type</c> hung every
///     Latest-Threads fan-out in the Blazor loading spinner).</item>
///   <item><b>MainNode fallback</b> — a NULL <c>main_node</c> resolves to the row's own path.</item>
///   <item><b>PreRenderedHtml mirror</b> — markdown content's prerendered HTML is surfaced on
///     <see cref="MeshNode.PreRenderedHtml"/> like the FileSystem/Caching adapters do; without it
///     the welcome page served from storage is blank.</item>
/// </list>
/// </summary>
internal static class SnowflakeMeshNodeReader
{
    /// <summary>
    /// Materializes a <see cref="MeshNode"/> from the current row of <paramref name="reader"/>.
    /// Expects at least <c>id</c>, <c>namespace</c>, <c>last_modified</c>, <c>version</c> and
    /// <c>state</c> in the projection; every other column (<c>name</c>, <c>description</c>,
    /// <c>node_type</c>, <c>category</c>, <c>icon</c>, <c>display_order</c>, <c>content</c>,
    /// <c>desired_id</c>, <c>main_node</c>, <c>sync_behavior</c>) is looked up by name and
    /// tolerated when absent — so mesh_nodes-shaped rows (with <c>sync_behavior</c>) and
    /// satellite/history rows (without it) read through the same code path.
    /// </summary>
    /// <param name="reader">Positioned data reader (a row must be current).</param>
    /// <param name="options">Serializer options used for polymorphic content deserialization.</param>
    /// <param name="logger">
    /// Optional logger; a poisoned content payload is reported here as a warning while the row
    /// itself still materializes (with <c>Content = null</c>).
    /// </param>
    internal static MeshNode ReadMeshNode(DbDataReader reader, JsonSerializerOptions options, ILogger? logger = null)
    {
        var id = reader.GetString(RequireOrdinal(reader, "id"));
        var ns = reader.GetString(RequireOrdinal(reader, "namespace"));

        object? content = null;
        var contentOrdinal = FindOrdinal(reader, "content");
        if (contentOrdinal >= 0 && !reader.IsDBNull(contentOrdinal))
        {
            // VARIANT comes back from the driver as JSON text — the same read shape as PG's
            // jsonb-as-text — but with alphabetized keys, so the discriminator is re-fronted
            // before deserialization.
            var json = EnsureTypeDiscriminatorFirst(reader.GetString(contentOrdinal));

            // A poisoned row (malformed polymorphic discriminator, an unknown $type, etc.)
            // must NOT take down the entire query. Degrade the content for THIS row only,
            // leaving the MeshNode skeleton intact so paths/names/timestamps still surface —
            // and surface the defect via the warning log so the bad payload gets fixed at the
            // source instead of silently vanishing.
            try
            {
                content = JsonSerializer.Deserialize<object>(json, options);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "[Snowflake] Skipping content for poisoned row {Path}: {Error}",
                    string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}", ex.Message);
            }
        }

        return new MeshNode(id, string.IsNullOrEmpty(ns) ? null : ns)
        {
            Name = GetStringOrNull(reader, "name"),
            Description = GetStringOrNull(reader, "description"),
            NodeType = GetStringOrNull(reader, "node_type"),
            Category = GetStringOrNull(reader, "category"),
            Icon = GetStringOrNull(reader, "icon"),
            Order = GetInt32OrNull(reader, "display_order"),
            LastModified = ReadUtcTimestamp(reader, RequireOrdinal(reader, "last_modified")),
            Version = ToInt64(reader.GetValue(RequireOrdinal(reader, "version"))),
            State = (MeshNodeState)ToInt16(reader.GetValue(RequireOrdinal(reader, "state"))),
            SyncBehavior = ReadSyncBehavior(reader),
            Content = content,
            // Mirror the prerendered HTML onto the top-level field, like the FileSystem/Caching
            // adapters do. Consumers that render straight from the node — e.g. the Space
            // Overview's BuildBodyContent — read MeshNode.PreRenderedHtml, not Content; without
            // this the welcome page served from Snowflake is blank. It's a transient mirror of
            // MarkdownContent.PrerenderedHtml, not a column.
            PreRenderedHtml = content is MarkdownContent { PrerenderedHtml: { Length: > 0 } html } ? html : null,
            DesiredId = GetStringOrNull(reader, "desired_id"),
            MainNode = GetStringOrNull(reader, "main_node")
                ?? (string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}")
        };
    }

    /// <summary>
    /// Reads the node-level <see cref="MeshNode.SyncBehavior"/> (the static-repo "Not synced"
    /// decouple claim) from a result row. The <c>sync_behavior</c> column lives only on
    /// <c>mesh_nodes</c> and the <c>access</c> satellite — reads from other satellite tables,
    /// from <c>mesh_node_history</c>, and from projections that omit the column default to
    /// <see cref="SyncBehavior.Include"/> (fully synced), the column's <c>DEFAULT 0</c>.
    /// Defensive lookup (scan field names rather than <c>GetOrdinal</c>) keeps every SELECT
    /// that omits the column working unchanged — mirroring <c>PgMeshNodeReader.ReadSyncBehavior</c>.
    /// </summary>
    internal static SyncBehavior ReadSyncBehavior(DbDataReader reader)
    {
        var ordinal = FindOrdinal(reader, "sync_behavior");
        if (ordinal < 0 || reader.IsDBNull(ordinal))
            return SyncBehavior.Include;
        return (SyncBehavior)ToInt16(reader.GetValue(ordinal));
    }

    /// <summary>
    /// Snowflake VARIANT reorders object keys alphabetically at ALL nesting levels (exactly like
    /// PG jsonb), which breaks System.Text.Json polymorphic deserialization (it requires
    /// <c>$type</c> as the first property). This method recursively moves <c>$type</c> to the
    /// front in every object throughout the JSON tree. No-op fast path when the payload carries
    /// no discriminator at all.
    /// </summary>
    internal static string EnsureTypeDiscriminatorFirst(string json)
    {
        if (!json.Contains("\"$type\"", StringComparison.Ordinal))
            return json; // No discriminator anywhere

        using var doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            WriteElementWithTypeFirst(writer, doc.RootElement);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Recursively writes a <see cref="JsonElement"/>, ensuring <c>$type</c> is the first
    /// property in every object.
    /// </summary>
    private static void WriteElementWithTypeFirst(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                // Write $type first if present
                if (element.TryGetProperty("$type", out var typeValue))
                {
                    writer.WritePropertyName("$type");
                    typeValue.WriteTo(writer);
                }
                // Write remaining properties (recursively)
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name == "$type")
                        continue;
                    writer.WritePropertyName(prop.Name);
                    WriteElementWithTypeFirst(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElementWithTypeFirst(writer, item);
                }
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// Reads a UTC <see cref="DateTimeOffset"/> from a timestamp column. <c>TIMESTAMP_NTZ</c>
    /// stores UTC by this backend's contract and typically surfaces as a <see cref="DateTime"/>
    /// with <see cref="DateTimeKind.Unspecified"/>, which is re-stamped as UTC. Defensive against
    /// driver/endpoint variance: when the provider hands back a <see cref="DateTimeOffset"/>
    /// directly (e.g. a <c>TIMESTAMP_TZ</c>-mapping emulator), it is normalized to UTC instead.
    /// </summary>
    private static DateTimeOffset ReadUtcTimestamp(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset dto => dto.ToUniversalTime(),
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero),
            _ => new DateTimeOffset(
                DateTime.SpecifyKind(Convert.ToDateTime(value, CultureInfo.InvariantCulture), DateTimeKind.Utc),
                TimeSpan.Zero)
        };
    }

    /// <summary>
    /// Finds a column ordinal by name, case-insensitively (unquoted Snowflake identifiers come
    /// back uppercased; quoted ones lowercase — the reader accepts either). Returns <c>-1</c>
    /// when the column is not part of the projection.
    /// </summary>
    private static int FindOrdinal(DbDataReader reader, string name)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Like <see cref="FindOrdinal"/> but throws for a column every node projection must carry
    /// (<c>id</c>, <c>namespace</c>, <c>last_modified</c>, <c>version</c>, <c>state</c>) —
    /// a missing required column is a defective SELECT, not a readable row.
    /// </summary>
    private static int RequireOrdinal(DbDataReader reader, string name)
    {
        var ordinal = FindOrdinal(reader, name);
        return ordinal >= 0
            ? ordinal
            : throw new InvalidOperationException(
                $"Required column '{name}' is missing from the Snowflake mesh-node projection.");
    }

    /// <summary>Reads a nullable text column by name; absent column or SQL NULL → <c>null</c>.</summary>
    private static string? GetStringOrNull(DbDataReader reader, string name)
    {
        var ordinal = FindOrdinal(reader, name);
        return ordinal < 0 || reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    /// <summary>Reads a nullable integer column by name; absent column or SQL NULL → <c>null</c>.</summary>
    private static int? GetInt32OrNull(DbDataReader reader, string name)
    {
        var ordinal = FindOrdinal(reader, name);
        return ordinal < 0 || reader.IsDBNull(ordinal)
            ? null
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Numeric coercion for Snowflake <c>NUMBER</c> columns, which the driver may surface as
    /// <see cref="long"/> or <see cref="decimal"/> depending on precision/scale metadata.
    /// </summary>
    private static long ToInt64(object value)
        => Convert.ToInt64(value, CultureInfo.InvariantCulture);

    /// <summary>See <see cref="ToInt64"/> — the <c>NUMBER(5,0)</c> (SMALLINT-shaped) variant.</summary>
    private static short ToInt16(object value)
        => Convert.ToInt16(value, CultureInfo.InvariantCulture);
}
