using System.Text.RegularExpressions;
using MeshWeaver.Hosting.PostgreSql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Forbids the SHAPE, not just the instance — this defect has now occurred twice in this one file.
///
/// <para>A trigger created under <c>IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'X')</c>
/// is installed on the FIRST schema to be provisioned and silently skipped on every schema after it,
/// because <c>pg_trigger</c> is a database-global catalog while trigger names are unique per TABLE.
/// V44 repaired exactly this for <c>mesh_node_copy_to_history</c>; the identical guard survived on
/// <c>mesh_node_notify</c> in three scripts — including <c>GetVersionedPartitionDdl</c>, the very
/// method V44 touched — until V54.</para>
///
/// <para>So these tests assert over the GENERATED SQL of every schema script, and the first one
/// rejects the pattern for ANY trigger name. A future trigger reintroducing the guard fails here
/// rather than in production, where the symptom is silence: writes that emit no notification, and
/// live views that simply stop updating.</para>
/// </summary>
public class MeshNodeNotifyTriggerScriptTests
{
    private static readonly PostgreSqlStorageOptions Options = new();

    public static TheoryData<string, string> AllSchemaScripts() => new()
    {
        { nameof(PostgreSqlSchemaInitializer.GetMeshSchemaScript),
          PostgreSqlSchemaInitializer.GetMeshSchemaScript(Options, "mesh_versions") },
        { nameof(PostgreSqlSchemaInitializer.GetVersionedPartitionDdl),
          PostgreSqlSchemaInitializer.GetVersionedPartitionDdl(1536, "\"acme\"") },
        { nameof(PostgreSqlSchemaInitializer.GetUnversionedSchemaScript),
          PostgreSqlSchemaInitializer.GetUnversionedSchemaScript(Options) },
    };

    /// <summary>
    /// 🚨 The class-level pin: NO trigger anywhere in these scripts may be gated on a bare
    /// <c>pg_trigger.tgname</c> probe. A guard that does not also constrain the table (via
    /// <c>tgrelid</c>/<c>pg_class</c>) or the schema is satisfied by an unrelated schema's trigger.
    /// </summary>
    /// <summary>
    /// Strips SQL line comments before analysis. The scripts document this very anti-pattern in
    /// their comments (quoting the old guard verbatim so the next reader understands why the form
    /// changed), and a scanner that cannot tell commentary from code would flag that prose —
    /// which is exactly what the first run of this test did.
    /// </summary>
    private static string WithoutComments(string sql) =>
        string.Join('\n', sql.Split('\n').Select(line =>
        {
            var idx = line.IndexOf("--", StringComparison.Ordinal);
            return idx >= 0 ? line[..idx] : line;
        }));

    [Theory]
    [MemberData(nameof(AllSchemaScripts))]
    public void NoTriggerIsGatedOnAGloballyScopedNameProbe(string scriptName, string sql)
    {
        // Any `pg_trigger` reference whose surrounding predicate never mentions pg_class/tgrelid
        // is schema-blind. Matching per-statement keeps a legitimate, properly-joined probe legal.
        foreach (Match m in Regex.Matches(WithoutComments(sql), @"pg_trigger[\s\S]{0,400}?(?=;|\bTHEN\b)",
                     RegexOptions.IgnoreCase))
        {
            var predicate = m.Value;
            var isTableScoped = predicate.Contains("tgrelid", StringComparison.OrdinalIgnoreCase)
                                || predicate.Contains("pg_class", StringComparison.OrdinalIgnoreCase);

            Assert.True(isTableScoped,
                $"{scriptName} probes pg_trigger without constraining the table or schema:\n"
                + $"{predicate.Trim()}\n\n"
                + "Trigger names are unique per TABLE, not per database, so this guard is satisfied "
                + "by ANY schema that already holds a trigger of that name — every later partition "
                + "schema then silently skips its own. Use the per-table form the rest of this file "
                + "uses: DROP TRIGGER IF EXISTS <name> ON <table>; CREATE TRIGGER <name> …  "
                + "(V44 fixed this for mesh_node_copy_to_history; V54 for mesh_node_notify.)");
        }
    }

    /// <summary>
    /// And the positive half: every script must actually CREATE the notify trigger, per table.
    /// A script that silently stopped creating it would pass the negative test above trivially —
    /// "finding nothing is not passing".
    /// </summary>
    [Theory]
    [MemberData(nameof(AllSchemaScripts))]
    public void EverySchemaScriptCreatesTheNotifyTriggerOnMeshNodes(string scriptName, string sql)
    {
        Assert.True(
            Regex.IsMatch(sql, @"DROP\s+TRIGGER\s+IF\s+EXISTS\s+mesh_node_notify\s+ON\s",
                RegexOptions.IgnoreCase),
            $"{scriptName} must drop mesh_node_notify per-table before creating it (idempotent "
            + "re-apply, and the form that cannot be skipped by another schema's trigger)");

        Assert.True(
            Regex.IsMatch(sql,
                @"CREATE\s+TRIGGER\s+mesh_node_notify\s+AFTER\s+INSERT\s+OR\s+UPDATE\s+OR\s+DELETE\s+ON\s+[^\s]*mesh_nodes",
                RegexOptions.IgnoreCase),
            $"{scriptName} must create mesh_node_notify on its own mesh_nodes — without it, writes "
            + "to that partition emit no pg_notify and nothing outside the writing process learns "
            + "the node changed");
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL. `WithoutComments` exists so the detector ignores the scripts' own
    /// prose about this anti-pattern — and a stripper that is slightly too eager would silently
    /// blind the detector instead, leaving a test that passes because it inspects nothing. So feed
    /// it the exact guard V44 and V54 removed and require it to still be caught.
    /// </summary>
    [Fact]
    public void TheDetectorStillCatchesTheRealGuard()
    {
        const string bad = """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesh_node_notify') THEN
                    CREATE TRIGGER mesh_node_notify
                        AFTER INSERT OR UPDATE OR DELETE ON mesh_nodes
                        FOR EACH ROW EXECUTE FUNCTION notify_mesh_node_changes();
                END IF;
            END;
            $$;
            """;

        Assert.ThrowsAny<Exception>(
            () => NoTriggerIsGatedOnAGloballyScopedNameProbe("synthetic", bad));
    }

    /// <summary>
    /// The complement of the control above: a properly table-scoped probe — the form V44 used —
    /// must still be ACCEPTED, so the rule forbids the defect rather than the catalog.
    /// </summary>
    [Fact]
    public void ATableScopedProbeIsStillAllowed()
    {
        const string good = """
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_trigger tg
                    JOIN pg_class c ON c.oid = tg.tgrelid
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE tg.tgname = 'some_trigger'
                      AND c.relname = 'mesh_nodes' AND n.nspname = 'acme') THEN
                    CREATE TRIGGER some_trigger AFTER INSERT ON "acme".mesh_nodes
                        FOR EACH ROW EXECUTE FUNCTION f();
                END IF;
            END;
            $$;
            """;

        NoTriggerIsGatedOnAGloballyScopedNameProbe("synthetic-good", good);
    }

    /// <summary>
    /// The satellite tables were never affected (their script always used the per-table form), and
    /// that asymmetry is precisely why the defect survived so long: a partition's _Access/_Thread/
    /// _Activity writes notified while its mesh_nodes writes did not. Pin the good side too, so a
    /// future "consistency" refactor cannot regress the satellites onto the broken guard.
    /// </summary>
    [Fact]
    public void TheSatelliteTableScriptAlsoUsesThePerTableForm()
    {
        var sql = PostgreSqlSchemaInitializer.GetSatelliteTableScript("threads", 1536);

        Assert.Contains("DROP TRIGGER IF EXISTS \"threads_notify\" ON \"threads\"", sql);
        Assert.Contains("CREATE TRIGGER \"threads_notify\"", sql);
        Assert.DoesNotContain("pg_trigger", sql);
    }
}
