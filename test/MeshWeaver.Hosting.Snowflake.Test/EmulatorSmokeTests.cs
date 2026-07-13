using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace MeshWeaver.Hosting.Snowflake.Test;

/// <summary>
/// One fact per risky Snowflake construct the backend relies on, asserting round-trip
/// CORRECTNESS (not just no-throw) against the connected endpoint — the LocalStack emulator
/// by default, or a real account via <c>SNOWFLAKE_CONNECTION</c>. Together these pin the
/// emulator's capability baseline: when the emulator (which transpiles to another engine)
/// regresses or gains a construct, exactly one fact flips. Endpoint-optional constructs
/// (<c>VECTOR</c>, <c>LIKE … ESCAPE</c>) are gated on the probed
/// <see cref="SnowflakeCapabilities"/>; everything else is expected everywhere.
/// Scratch tables live in the central <c>public</c> schema under <c>smoke_*</c> names —
/// outside <see cref="SnowflakeFixture.CleanDataAsync"/>'s data-table list — and every fact
/// resets its own rows, so facts stay order-independent and re-runnable against a persistent
/// real account.
/// </summary>
[Collection("Snowflake")]
public class EmulatorSmokeTests(SnowflakeFixture fixture)
{
    [Fact]
    public async Task Merge_Upsert_RoundTrips()
    {
        fixture.SkipUnlessAvailable();
        var source = fixture.ConnectionSource;

        await source.ExecuteNonQuery(
                """CREATE TABLE IF NOT EXISTS "public"."smoke_merge" ("id" TEXT, "value" TEXT)""")
            .Should().Within(30.Seconds()).Emit();
        await source.ExecuteNonQuery("""DELETE FROM "public"."smoke_merge" """)
            .Should().Within(30.Seconds()).Emit();

        // First MERGE takes the NOT MATCHED branch (insert)…
        await source.ExecuteNonQuery(
                """
                MERGE INTO "public"."smoke_merge" t
                USING (SELECT :p0 AS "id", :p1 AS "value") s ON t."id" = s."id"
                WHEN MATCHED THEN UPDATE SET "value" = s."value"
                WHEN NOT MATCHED THEN INSERT ("id", "value") VALUES (s."id", s."value")
                """, [("p0", "k1"), ("p1", "v1")])
            .Should().Within(30.Seconds()).Emit();

        // …the second, same key, takes the MATCHED branch (update in place, no duplicate).
        await source.ExecuteNonQuery(
                """
                MERGE INTO "public"."smoke_merge" t
                USING (SELECT :p0 AS "id", :p1 AS "value") s ON t."id" = s."id"
                WHEN MATCHED THEN UPDATE SET "value" = s."value"
                WHEN NOT MATCHED THEN INSERT ("id", "value") VALUES (s."id", s."value")
                """, [("p0", "k1"), ("p1", "v2")])
            .Should().Within(30.Seconds()).Emit();

        var values = await source.Rows(
                """SELECT "value" FROM "public"."smoke_merge" WHERE "id" = :p0""",
                [("p0", "k1")], r => r.GetString(0))
            .Should().Within(30.Seconds()).Emit();
        values.Should().Equal("v2"); // exactly one row, updated — the upsert round-trip
    }

    [Fact]
    public async Task Identity_AutoIncrement_IsMonotone()
    {
        fixture.SkipUnlessAvailable();
        var source = fixture.ConnectionSource;

        await source.ExecuteNonQuery(
                """
                CREATE TABLE IF NOT EXISTS "public"."smoke_identity" (
                    "seq"    NUMBER(19,0) IDENTITY(1,1) NOT NULL,
                    "marker" NUMBER(10,0) NOT NULL
                )
                """)
            .Should().Within(30.Seconds()).Emit();
        await source.ExecuteNonQuery("""DELETE FROM "public"."smoke_identity" """)
            .Should().Within(30.Seconds()).Emit();

        // Three separate INSERTs — the driver executes one statement per command.
        foreach (var marker in new[] { 1, 2, 3 })
            await source.ExecuteNonQuery(
                    """INSERT INTO "public"."smoke_identity" ("marker") VALUES (:p0)""",
                    [("p0", marker)])
                .Should().Within(30.Seconds()).Emit();

        var seqs = await source.Rows(
                """SELECT "seq" FROM "public"."smoke_identity" ORDER BY "marker" """,
                [], r => r.GetInt64(0))
            .Should().Within(30.Seconds()).Emit();

        // Strictly increasing in insertion order (DELETE does not reset the identity —
        // monotonicity, not a starting value, is the contract the event_log seq relies on).
        seqs.Should().HaveCount(3);
        seqs.Distinct().Should().HaveCount(3);
        seqs.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task TimestampNtz_UtcInstant_RoundTrips()
    {
        fixture.SkipUnlessAvailable();
        var source = fixture.ConnectionSource;

        await source.ExecuteNonQuery(
                """CREATE TABLE IF NOT EXISTS "public"."smoke_ts" ("id" TEXT, "at" TIMESTAMP_NTZ)""")
            .Should().Within(30.Seconds()).Emit();
        await source.ExecuteNonQuery("""DELETE FROM "public"."smoke_ts" """)
            .Should().Within(30.Seconds()).Emit();

        // Sub-millisecond ticks on purpose: TIMESTAMP_NTZ(9) must hold .NET's 100 ns
        // resolution exactly — the backend stores UTC instants as NTZ (the SYSDATE() twin
        // of PG's TIMESTAMPTZ NOW()), binding DateTimeOffset.UtcDateTime.
        var instant = new DateTimeOffset(2026, 3, 5, 12, 34, 56, TimeSpan.Zero).AddTicks(7_891_234);
        await source.ExecuteNonQuery(
                """INSERT INTO "public"."smoke_ts" ("id", "at") VALUES (:p0, :p1)""",
                [("p0", "t1"), ("p1", instant.UtcDateTime)])
            .Should().Within(30.Seconds()).Emit();

        var readBack = await source.Probe(
                """SELECT "at" FROM "public"."smoke_ts" WHERE "id" = :p0""",
                [("p0", "t1")], r => r.GetDateTime(0))
            .Should().Within(30.Seconds()).Emit();
        readBack.Should().Be(instant.UtcDateTime); // same instant, tick-exact
    }

    [Fact]
    public async Task TryParseJson_VariantFieldExtraction_RoundTrips()
    {
        fixture.SkipUnlessAvailable();
        var source = fixture.ConnectionSource;

        await source.ExecuteNonQuery(
                """CREATE TABLE IF NOT EXISTS "public"."smoke_json" ("id" TEXT, "content" VARIANT)""")
            .Should().Within(30.Seconds()).Emit();
        await source.ExecuteNonQuery("""DELETE FROM "public"."smoke_json" """)
            .Should().Within(30.Seconds()).Emit();

        // INSERT … SELECT TRY_PARSE_JSON(:param) — the adapter's write shape for VARIANT
        // columns (a plain string bind would store a quoted scalar, not an object).
        await source.ExecuteNonQuery(
                """INSERT INTO "public"."smoke_json" ("id", "content") SELECT :p0, TRY_PARSE_JSON(:p1)""",
                [("p0", "j1"), ("p1", """{"status":"open","owner":{"name":"kim"}}""")])
            .Should().Within(30.Seconds()).Emit();

        // content:field::string — the query layer's variant accessor, one level and nested.
        var (status, ownerName) = await source.Probe(
                """
                SELECT "content":"status"::string, "content":"owner"."name"::string
                FROM "public"."smoke_json" WHERE "id" = :p0
                """,
                [("p0", "j1")], r => (r.GetString(0), r.GetString(1)))
            .Should().Within(30.Seconds()).Emit();
        status.Should().Be("open");
        ownerName.Should().Be("kim");

        // TRY_PARSE_JSON is the non-throwing parse: malformed input yields NULL, not an error.
        var malformedIsNull = await source.Probe(
                "SELECT TRY_PARSE_JSON('not json') IS NULL", [], r => r.GetBoolean(0))
            .Should().Within(30.Seconds()).Emit();
        malformedIsNull.Should().BeTrue();
    }

    [Fact]
    public async Task Ilike_MatchesCaseInsensitively()
    {
        fixture.SkipUnlessAvailable();
        var source = fixture.ConnectionSource;

        await source.ExecuteNonQuery(
                """CREATE TABLE IF NOT EXISTS "public"."smoke_ilike" ("name" TEXT)""")
            .Should().Within(30.Seconds()).Emit();
        await source.ExecuteNonQuery("""DELETE FROM "public"."smoke_ilike" """)
            .Should().Within(30.Seconds()).Emit();

        foreach (var name in new[] { "Laptop Pro", "desktop tower", "LAPTOP air" })
            await source.ExecuteNonQuery(
                    """INSERT INTO "public"."smoke_ilike" ("name") VALUES (:p0)""", [("p0", name)])
                .Should().Within(30.Seconds()).Emit();

        var matches = await source.Rows(
                """SELECT "name" FROM "public"."smoke_ilike" WHERE "name" ILIKE :p0""",
                [("p0", "%laptop%")], r => r.GetString(0))
            .Should().Within(30.Seconds()).Emit();

        // Both casings match, the non-matching row doesn't — case-insensitivity, not collation
        // order, is the pinned behavior.
        matches.Should().HaveCount(2);
        matches.Should().Contain("Laptop Pro");
        matches.Should().Contain("LAPTOP air");
    }

    [Fact]
    public async Task DropSchemaCascade_RemovesTables()
    {
        fixture.SkipUnlessAvailable();
        var source = fixture.ConnectionSource;

        await source.ExecuteNonQuery("""CREATE SCHEMA IF NOT EXISTS "smoke_dropme" """)
            .Should().Within(30.Seconds()).Emit();
        await source.ExecuteNonQuery(
                """CREATE TABLE IF NOT EXISTS "smoke_dropme"."t1" ("id" TEXT)""")
            .Should().Within(30.Seconds()).Emit();

        var before = await source.ScalarLong(
                "SELECT COUNT(*) FROM information_schema.tables WHERE LOWER(table_schema) = 'smoke_dropme'")
            .Should().Within(30.Seconds()).Emit();
        before.Should().Be(1);

        // Space deletion drops the whole partition schema this way.
        await source.ExecuteNonQuery("""DROP SCHEMA "smoke_dropme" CASCADE""")
            .Should().Within(30.Seconds()).Emit();

        var schemaCount = await source.ScalarLong(
                "SELECT COUNT(*) FROM information_schema.schemata WHERE LOWER(schema_name) = 'smoke_dropme'")
            .Should().Within(30.Seconds()).Emit();
        schemaCount.Should().Be(0);
        var tableCount = await source.ScalarLong(
                "SELECT COUNT(*) FROM information_schema.tables WHERE LOWER(table_schema) = 'smoke_dropme'")
            .Should().Within(30.Seconds()).Emit();
        tableCount.Should().Be(0);
    }

    [Fact]
    public async Task InformationSchemaSchemata_SeesCreatedSchema()
    {
        fixture.SkipUnlessAvailable();
        var source = fixture.ConnectionSource;

        // Quoted-lowercase creation + LOWER() comparison — the exact pattern the partition
        // existence checks and CleanDataAsync's catalog listing rely on.
        await source.ExecuteNonQuery("""CREATE SCHEMA IF NOT EXISTS "smoke_catalog" """)
            .Should().Within(30.Seconds()).Emit();

        var count = await source.ScalarLong(
                "SELECT COUNT(*) FROM information_schema.schemata WHERE LOWER(schema_name) = 'smoke_catalog'")
            .Should().Within(30.Seconds()).Emit();
        count.Should().Be(1);
    }

    [Fact]
    public async Task Vector_CosineSimilarity_OrdersByCloseness()
    {
        fixture.SkipUnlessAvailable();
        fixture.SkipUnless(c => c.SupportsVector,
            "endpoint lacks VECTOR(FLOAT, N) — real Snowflake supports it; the emulator may not");
        var source = fixture.ConnectionSource;

        await source.ExecuteNonQuery(
                """CREATE TABLE IF NOT EXISTS "public"."smoke_vec" ("id" TEXT, "v" VECTOR(FLOAT, 3))""")
            .Should().Within(30.Seconds()).Emit();
        await source.ExecuteNonQuery("""DELETE FROM "public"."smoke_vec" """)
            .Should().Within(30.Seconds()).Emit();

        foreach (var (id, vector) in new[] { ("x", "[1,0,0]"), ("y", "[0.9,0.1,0]"), ("z", "[0,1,0]") })
            await source.ExecuteNonQuery(
                    """INSERT INTO "public"."smoke_vec" ("id", "v") SELECT :p0, PARSE_JSON(:p1)::VECTOR(FLOAT, 3)""",
                    [("p0", id), ("p1", vector)])
                .Should().Within(30.Seconds()).Emit();

        // Cosine similarity to [1,0,0]: x (identical) > y (close) > z (orthogonal) —
        // the ordering the vector-search path's ORDER BY … DESC produces.
        var ordered = await source.Rows(
                """
                SELECT "id" FROM "public"."smoke_vec"
                ORDER BY VECTOR_COSINE_SIMILARITY("v", PARSE_JSON('[1,0,0]')::VECTOR(FLOAT, 3)) DESC
                """,
                [], r => r.GetString(0))
            .Should().Within(30.Seconds()).Emit();
        ordered.Should().Equal("x", "y", "z");
    }

    [Fact]
    public async Task LikeEscape_TreatsEscapedUnderscoreAsLiteral()
    {
        fixture.SkipUnlessAvailable();
        fixture.SkipUnless(c => c.SupportsLikeEscape,
            "endpoint lacks LIKE … ESCAPE — the SQL generator falls back when unsupported");
        var source = fixture.ConnectionSource;

        await source.ExecuteNonQuery(
                """CREATE TABLE IF NOT EXISTS "public"."smoke_like" ("name" TEXT)""")
            .Should().Within(30.Seconds()).Emit();
        await source.ExecuteNonQuery("""DELETE FROM "public"."smoke_like" """)
            .Should().Within(30.Seconds()).Emit();

        foreach (var name in new[] { "a_b", "axb" })
            await source.ExecuteNonQuery(
                    """INSERT INTO "public"."smoke_like" ("name") VALUES (:p0)""", [("p0", name)])
                .Should().Within(30.Seconds()).Emit();

        // Without ESCAPE, '_' is a single-character wildcard — both rows match…
        var unescaped = await source.ScalarLong(
                """SELECT COUNT(*) FROM "public"."smoke_like" WHERE "name" LIKE 'a_b'""")
            .Should().Within(30.Seconds()).Emit();
        unescaped.Should().Be(2);

        // …with ESCAPE, the escaped underscore is a literal — only 'a_b' matches. Snowflake
        // string literals treat backslash as an escape, so '\\' in the SQL text is ONE
        // backslash (byte-identical to the capability probe's statement).
        var escaped = await source.ScalarLong(
                """SELECT COUNT(*) FROM "public"."smoke_like" WHERE "name" LIKE 'a\\_b' ESCAPE '\\'""")
            .Should().Within(30.Seconds()).Emit();
        escaped.Should().Be(1);
    }
}
