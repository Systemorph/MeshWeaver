using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Drop schemas accidentally created from path segments.
///
/// Bug: paths like <c>login</c>, <c>markdown</c>, <c>onboarding</c> etc. created Postgres
/// schemas that shouldn't exist as partitions. This migration drops them so partition
/// discovery stays clean.
/// </summary>
public sealed class V03_DropRogueSchemas : IMigration
{
    private static readonly string[] RogueSchemas =
    [
        "_access", "_address_", "_graph", "_settings", "_tracking", "_thread", "_source", "_test",
        "login", "markdown", "onboarding", "welcome", "settings", "storage",
        "p", "mesh", "thread", "agent", "partition", "organization", "vuser"
    ];

    public int Version => 3;
    public string Description => "Drop rogue schemas created from path segments";

    public async Task RunAsync(MigrationContext ctx)
    {
        foreach (var rogue in RogueSchemas)
        {
            try
            {
                await using (var dropCmd = ctx.DataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{rogue}\" CASCADE"))
                    await dropCmd.ExecuteNonQueryAsync();
                await using (var dropVCmd = ctx.DataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{rogue}_versions\" CASCADE"))
                    await dropVCmd.ExecuteNonQueryAsync();
                ctx.Logger.LogInformation("Repair v3: Dropped rogue schema {Schema}", rogue);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Repair v3: Failed to drop schema {Schema}", rogue);
            }
        }
    }
}
