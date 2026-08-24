#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Every command in <c>PostgreSqlSchemaInitializer</c> must carry the maintenance timeout, not
/// Npgsql's 30-second default.
///
/// <para>🚨 <b>Why this is a test and not a code review note.</b> The default is a REQUEST-path
/// number, and nothing in that class is on a request path: it is boot-time DDL plus a sweep over
/// EVERY partition schema (the auth-mirror self-heal). That sweep scales with the instance, so the
/// default turns "this deployment is large" into a migration that cannot finish — and the symptom
/// is nothing like the cause. On memex.meshweaver.cloud (2026-08-22) the migration
/// CrashLoopBackOff'd with <c>NpgsqlException: Exception while reading from stream ---&gt;
/// TimeoutException</c>, the portal's new pods never became ready, and the rollout stalled at 1/5
/// while the same image migrated a smaller instance fine.</para>
///
/// <para>A new command added here without the timeout reintroduces exactly that, on whichever
/// instance is biggest — which is always the one you least want to find out on. Source-level
/// because the failure needs a large production database to reproduce, and a test that cannot run
/// is not a guard.</para>
/// </summary>
public class SchemaInitCommandTimeoutTests
{
    [Fact]
    public void EverySchemaInitCommand_UsesTheMaintenanceTimeout()
    {
        var source = ReadInitializerSource();

        // The helper itself is the one legitimate raw CreateCommand — it is what applies the timeout.
        // Its bound is the maintenance default unless the CALLER supplies one (the batched heal
        // passes a modest per-batch ceiling; bounded work must not borrow the whole-sweep bound).
        var helperBody = Between(source, "private static NpgsqlCommand CreateMaintenanceCommand", "\n    }");
        Assert.Contains("int timeoutSeconds = MaintenanceCommandTimeoutSeconds", helperBody, StringComparison.Ordinal);
        Assert.Contains("CommandTimeout = timeoutSeconds", helperBody, StringComparison.Ordinal);
        Assert.Contains("dataSource.CreateCommand(sql)", helperBody, StringComparison.Ordinal);

        // …and it must not call itself. A regex-driven rewrite once turned this into infinite
        // recursion, which builds cleanly and stack-overflows every migration.
        Assert.DoesNotContain("dataSource.CreateMaintenanceCommand(", helperBody, StringComparison.Ordinal);

        var offenders = Regex.Matches(source.Replace(helperBody, string.Empty),
                @"\b(?<recv>dataSource|schemaDataSource|baseDataSource|versionsDataSource)\.CreateCommand\(")
            .Select(m => m.Groups["recv"].Value)
            .ToList();

        Assert.True(offenders.Count == 0,
            "these data-source commands bypass the maintenance timeout and will inherit Npgsql's "
            + $"30s request default: {string.Join(", ", offenders)}. Use CreateMaintenanceCommand.");
    }

    [Fact]
    public void TheTimeoutIsGenerousButBounded()
    {
        var source = ReadInitializerSource();
        var match = Regex.Match(source, @"MaintenanceCommandTimeoutSeconds\s*=\s*(?<n>\d+)");
        Assert.True(match.Success, "the maintenance timeout constant must exist and be a literal");

        var seconds = int.Parse(match.Groups["n"].Value);
        // Generous enough for a whole-instance sweep…
        Assert.True(seconds >= 300, $"a whole-instance sweep needs room; {seconds}s is too tight");
        // …and bounded, so a migration wedged on a lock fails instead of hanging a rollout forever.
        Assert.True(seconds <= 1800, $"{seconds}s would let a wedged migration hang a rollout");
    }

    private static string ReadInitializerSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.SkipWhen(dir is null, "repository tree not reachable — source guard runs in-repo only");

        var path = Path.Combine(dir!.FullName,
            "src", "MeshWeaver.Hosting.PostgreSql", "PostgreSqlSchemaInitializer.cs");
        Assert.True(File.Exists(path), $"expected the initializer at {path}");
        return File.ReadAllText(path);
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"could not find '{start}'");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"could not find '{end}' after '{start}'");
        return source[from..to];
    }
}
