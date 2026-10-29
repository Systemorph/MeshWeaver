using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 MeshWeaver#7316 — a module whose files come from a TRUNCATED tree listing
/// (<see cref="RepoSnapshot.ListingIsComplete"/> false) is never <c>Materialized</c>.
///
/// <para>#6742 made an EMPTY selection from a truncated listing indeterminate rather than absent.
/// The partial-presence case stayed open: when the listing still named SOME of the module's files
/// and no recorded module version had to be proved (an exact install commit), the module was
/// written <c>Materialized</c> over an incomplete file set, and read as verified.</para>
/// </summary>
public sealed class APartiallyListedModuleIsNeverMaterializedTest : IDisposable
{
    private const string Repo = "https://github.com/Systemorph/ComboFixture";
    private static readonly string InstalledCommit = new('1', 40);
    private const string Version = "aaaaaaaaaaaaaaaa";

    private readonly string workRoot = Path.Combine(
        Path.GetTempPath(), $"combo-partial-{Guid.NewGuid():N}"[..36]);

    public void Dispose()
    {
        if (Directory.Exists(workRoot))
            Directory.Delete(workRoot, recursive: true);
    }

    /// <summary>The defect: a truncated tree that still names the module's files.</summary>
    [Fact]
    public async Task AModuleFromATruncatedListing_IsFailedIndeterminate_NotMaterialized()
    {
        var report = await Assemble(complete: false);

        var widget = report.Modules.Single();
        widget.Status.Should().Be(ModuleAssemblyStatus.Failed,
            "a truncated listing cannot show the module's file set is complete");
        widget.Error.Should().Contain("indeterminate listing");
        Directory.Exists(Path.Combine(workRoot, "Widget")).Should().BeFalse(
            "an incomplete file set is never written as though it were the module");
    }

    /// <summary>NEGATIVE CONTROL — the same tree, listed COMPLETELY, materialises.</summary>
    [Fact]
    public async Task TheSameModuleFromACompleteListing_IsMaterialized()
    {
        var report = await Assemble(complete: true);

        var widget = report.Modules.Single();
        widget.Status.Should().Be(ModuleAssemblyStatus.Materialized, widget.Error ?? "");
    }

    private Task<ComboAssemblyReport> Assemble(bool complete)
    {
        IReadOnlyList<RepoFile> files =
        [
            new RepoFile("Widget/index.json", """{ "id": "Widget", "nodeType": "Space", "name": "Widget" }"""),
            new RepoFile($"Widget/{ModuleManifest.FileName}", $$"""
                { "module": "Widget", "moduleVersion": "{{Version}}", "version": "1.0.0", "files": {} }
                """),
        ];
        var assembler = new InstanceComboAssembler(
            (_, gitRef, _, _) => Observable.Return(
                new RepoSnapshot(gitRef.Length == 40 ? gitRef : InstalledCommit, files.ToImmutableList())
                {
                    ListingIsComplete = complete,
                }),
            IoPool.Unbounded,
            new ComboAssemblyOptions { DefaultSourceRepository = Repo });
        var combo = new InstanceCombo
        {
            ReadAt = DateTimeOffset.UtcNow,
            Modules =
            [
                new ModuleCoordinate
                {
                    ModuleId = "Widget",
                    Package = new PackageCoordinate
                    {
                        RecordPath = "Plugins/Widget",
                        PackageId = "Widget",
                        SourceName = "Plugins",
                        ModuleVersion = Version,
                        InstalledFromRef = InstalledCommit,
                    },
                },
            ],
        };
        return assembler.Assemble(combo, workRoot)
            .Timeout(TimeSpan.FromSeconds(60))
            .Await(TestContext.Current.CancellationToken);
    }
}
