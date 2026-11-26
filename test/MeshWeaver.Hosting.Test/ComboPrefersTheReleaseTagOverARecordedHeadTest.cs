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
/// 🚨 MeshWeaver#4413 — an install record written before the commit was recorded carries
/// <c>installedFromRef: "HEAD"</c>. That moving ref used to SHADOW the module's release tag
/// (<c>{Module}/v{version}</c>), so every package that had not updated since failed the
/// moduleVersion proof against a newer HEAD and the whole combo became NotVerifiable (8 in a row
/// on the control instance). The tag names the installed bytes; it must be the ref fetched.
/// </summary>
public sealed class ComboPrefersTheReleaseTagOverARecordedHeadTest : IDisposable
{
    private const string Repo = "https://github.com/Systemorph/ComboFixture";
    private const string Installed = "aaaaaaaaaaaaaaaa";
    private const string Moved = "bbbbbbbbbbbbbbbb";

    private readonly string workRoot = Path.Combine(
        Path.GetTempPath(), $"combo-tag-{Guid.NewGuid().ToString("N")[..12]}");

    public void Dispose()
    {
        if (Directory.Exists(workRoot))
            Directory.Delete(workRoot, recursive: true);
    }

    [Fact]
    public async Task ARecordedHead_DoesNotShadowTheReleaseTag_SoTheInstalledBytesProveThemselves()
    {
        var (report, refs) = await Assemble(installedRef: "HEAD", version: "1.0.21");

        var widget = report.Modules.Single();
        widget.Status.Should().Be(ModuleAssemblyStatus.Materialized, widget.Error ?? "");
        widget.Pin.Should().Be(MaterializationPin.VerifiedModuleVersion);
        widget.RequestedRef.Should().Be("Widget/v1.0.21");
        refs.Should().ContainSingle().Which.Should().Be("Widget/v1.0.21");
    }

    /// <summary>NEGATIVE CONTROL — with no version to name a tag, HEAD has moved: still a named failure.</summary>
    [Fact]
    public async Task ARecordedHeadWithNoVersion_StillFailsTheProofAgainstAMovedHead()
    {
        var (report, _) = await Assemble(installedRef: "HEAD", version: null);

        var widget = report.Modules.Single();
        widget.Status.Should().Be(ModuleAssemblyStatus.Failed);
        widget.Error.Should().Contain("moduleVersion mismatch");
    }

    private async Task<(ComboAssemblyReport Report, List<string> Refs)> Assemble(string installedRef, string? version)
    {
        var refs = new List<string>();
        IObservable<RepoSnapshot> Fetch(string gitRef)
        {
            refs.Add(gitRef);
            // The release tag holds the installed bytes; every other ref (HEAD) has moved on.
            var moduleVersion = gitRef.StartsWith("Widget/v", StringComparison.Ordinal) ? Installed : Moved;
            IReadOnlyList<RepoFile> files =
            [
                new RepoFile("Widget/index.json", """{ "id": "Widget", "nodeType": "Space", "name": "Widget" }"""),
                new RepoFile($"Widget/{ModuleManifest.FileName}", $$"""
                    { "module": "Widget", "moduleVersion": "{{moduleVersion}}", "version": "1.0.0", "files": {} }
                    """),
            ];
            return Observable.Return(new RepoSnapshot(new string('2', 40), files.ToImmutableList()));
        }
        var assembler = new InstanceComboAssembler(
            (_, gitRef, _, _) => Fetch(gitRef),
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
                        ModuleVersion = Installed,
                        InstalledFromRef = installedRef,
                        Version = version,
                    },
                },
            ],
        };
        var report = await assembler.Assemble(combo, workRoot)
            .Timeout(TimeSpan.FromSeconds(60))
            .Await(TestContext.Current.CancellationToken);
        return (report, refs);
    }
}
