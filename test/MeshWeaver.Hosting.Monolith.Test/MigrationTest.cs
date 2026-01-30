using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests and utility for running the migration.
/// </summary>
public class MigrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string _samplesDataDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "Graph", "Data"));
    private readonly string _samplesContentDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "Graph", "content"));
    private JsonSerializerOptions JsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;

    [Fact(Skip = "Run manually to see what would be migrated")]
    public async Task DryRun_ShowsWhatWouldBeMigrated()
    {
        var utility = new MigrationUtility(_samplesDataDir, _samplesContentDir, JsonOptions);
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
        var utility = new MigrationUtility(_samplesDataDir, _samplesContentDir, JsonOptions);
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
