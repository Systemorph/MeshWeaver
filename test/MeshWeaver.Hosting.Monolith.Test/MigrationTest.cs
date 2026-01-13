using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests and utility for running the migration.
/// </summary>
public class MigrationTest(ITestOutputHelper output)
{
    private readonly string _samplesDataDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "Graph", "Data"));
    private readonly string _samplesContentDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "Graph", "content"));

    [Fact(Skip = "Run manually to see what would be migrated")]
    public async Task DryRun_ShowsWhatWouldBeMigrated()
    {
        var utility = new MigrationUtility(_samplesDataDir, _samplesContentDir);
        var report = await utility.MigrateAllAsync(dryRun: true);

        output.WriteLine(report.ToString());
        output.WriteLine("");
        output.WriteLine("Files that would be migrated:");
        foreach (var result in report.Results.Where(r => r.Status == MigrationStatus.WouldMigrate))
        {
            output.WriteLine($"  {result.SourceFile} -> {result.TargetFile} ({result.MigrationType})");
        }
    }

    [Fact(Skip = "Run manually to perform actual migration")]
    public async Task RunMigration_MigratesAllFiles()
    {
        var utility = new MigrationUtility(_samplesDataDir, _samplesContentDir);
        var report = await utility.MigrateAllAsync(dryRun: false);

        output.WriteLine(report.ToString());
        output.WriteLine("");
        output.WriteLine("Migrated files:");
        foreach (var result in report.Results.Where(r => r.Status == MigrationStatus.Migrated))
        {
            output.WriteLine($"  {result.SourceFile} -> {result.TargetFile}");
            if (result.ExtractedIcon != null)
                output.WriteLine($"    Extracted icon: {result.ExtractedIcon}");
        }
    }
}
