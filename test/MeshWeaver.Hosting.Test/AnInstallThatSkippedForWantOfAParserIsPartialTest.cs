using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 An install that skipped a file because the parser its type needs is not registered on its host
/// (a <c>nodeType: Skill</c> file on a 4.x pod whose AI module had not landed — pearl, 2026-11-26)
/// must NOT stamp the record complete-and-current. It records the files as PARTIAL
/// (<see cref="PackageManifest.PendingParserFiles"/>), and the next install pass retries once the
/// parser is available. Real mesh, the real installer and the real install funnel.
/// </summary>
public class AnInstallThatSkippedForWantOfAParserIsPartialTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Package = "PartialPkg";
    private const string SkillPath = "PartialPkg/Skill/hello.md";
    private const string SkillFile = "---\nnodeType: Skill\nname: Hello\n---\nSay hello.\n";

    /// <summary>The AI module's file parser, switchable: claims <c>nodeType: Skill|Agent</c> files only while enabled.</summary>
    private sealed class SwitchableAiParser : IFileFormatParser
    {
        public volatile bool Enabled;
        public IReadOnlyList<string> SupportedExtensions { get; } = [".md"];

        public MeshNode? Parse(string filePath, string content, string relativePath)
        {
            if (!Enabled)
                return null;
            var type = content.Contains("nodeType: Skill", StringComparison.Ordinal) ? "Skill"
                : content.Contains("nodeType: Agent", StringComparison.Ordinal) ? "Agent" : null;
            return type is null
                ? null
                : new MeshNode("hello", "Skill") { NodeType = type, Name = "Hello", Content = new AiStub("Say hello.") };
        }

        public string Serialize(MeshNode node) => "";
        public bool CanSerialize(MeshNode node) => false;
    }

    private sealed record AiStub(string Instructions);

    private readonly SwitchableAiParser _parser = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .AddPluginCatalog()
            // The Skill type is the AI module's; a bare static node stands in so the install can place one.
            .AddMeshNodes(new MeshNode("Skill") { Name = "Skill", NodeType = "NodeType" })
            .ConfigureServices(s => s.AddSingleton<IFileFormatParser>(_parser));

    private static PackageManifest Manifest(string moduleVersion) => new()
    {
        Id = Package, Name = Package, Kind = PackageKind.NodeRepo, TargetPartition = Package,
        SourceFolder = Package, Version = "1.0.0", ModuleVersion = moduleVersion,
        Source = "Plugins", WriteIntent = PackageRecordWriteIntent.Person,
    };

    private static string Node(string id, string ns, string text) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["$type"] = "MeshNode", ["id"] = id, ["namespace"] = ns, ["name"] = id, ["nodeType"] = "Markdown",
        ["content"] = new Dictionary<string, object> { ["$type"] = "MarkdownContent", ["content"] = text },
    });

    private static PackageFile[] Files(bool withSkill = true) =>
    [
        new($"{Package}/index.json", Node(Package, "", "# root")),
        new($"{Package}/Notes.json", Node("Notes", Package, "# notes")),
        .. withSkill ? new[] { new PackageFile(SkillPath, SkillFile) } : [],
    ];

    private sealed class FilesSource(PackageFile[] files) : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return((IReadOnlyList<PackageManifest>)[]);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            Observable.Return((IReadOnlyList<PackageFile>)files);
    }

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private Task<PackageManifest?> Record(CancellationToken ct) =>
        Storage.Read($"{PackageInstaller.InstalledPartition}/{Package}", Mesh.JsonSerializerOptions).Take(1)
            .DefaultIfEmpty(null)
            .Select(n => n?.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions))
            .Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<MeshNode?> Stored(string path, CancellationToken ct) =>
        Storage.Read(path, Mesh.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
            .Timeout(TestTimeouts.Convergence).Await(ct);

    private FileFormatParserRegistry Registry() =>
        new(Mesh.JsonSerializerOptions, Mesh.ServiceProvider.GetServices<IFileFormatParser>());

    [Fact(Timeout = 180_000)]
    public async Task AMissingParser_GivesAPartialRecordNamingTheFile_AndTheRetryCompletesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new FilesSource(Files());

        // The parser is NOT registered: the install skips the skill file …
        await CatalogLayoutAreas.InstallOrUpdate(Mesh, source, "HEAD", Manifest("v1"), NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct);

        var partial = await Record(ct);
        Assert.NotNull(partial);
        // … and the record is PARTIAL: it names the skipped file instead of claiming it installed.
        Assert.Equal([SkillPath], partial!.PendingParserFiles!);
        Assert.Null(partial.UnreadableFiles?.FirstOrDefault(f => f == SkillPath));
        Assert.NotNull(await Stored($"{Package}/Notes", ct));
        Assert.Null(await Stored($"{Package}/Skill/hello", ct));
        // While the parser is still absent a retry would skip the same file again, so none is owed.
        Assert.False(PackageInstaller.OwesParserRetry(partial, Registry()));

        // The parser lands (the AI module activates). The record, same hash as the source, is owed a retry …
        _parser.Enabled = true;
        Assert.True(PackageInstaller.OwesParserRetry(partial, Registry()));

        // … and the next pass completes it and stamps the record clean.
        await CatalogLayoutAreas.InstallOrUpdate(Mesh, source, "HEAD", Manifest("v1"), NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct);

        var done = await Record(ct);
        Assert.True(done!.PendingParserFiles is null or { Count: 0 }, "the retry must clear the pending set");
        Assert.NotNull(await Stored($"{Package}/Skill/hello", ct));
        Assert.False(PackageInstaller.OwesParserRetry(done, Registry()));
    }

    [Fact(Timeout = 180_000)]
    public async Task ANormalInstall_StampsNoPendingFiles_AndOwesNoRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        _parser.Enabled = true;

        await CatalogLayoutAreas.InstallOrUpdate(Mesh, new FilesSource(Files()), "HEAD", Manifest("v1"), NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct);

        var record = await Record(ct);
        Assert.NotNull(record);
        Assert.True(record!.PendingParserFiles is null or { Count: 0 });
        Assert.NotNull(await Stored($"{Package}/Notes", ct));
        Assert.NotNull(await Stored($"{Package}/Skill/hello", ct));
        Assert.False(PackageInstaller.OwesParserRetry(record, Registry()));
    }

    [Fact]
    public void ARecordWithNoSkippedFiles_OwesNoRetry_EvenWithTheParserRegistered()
    {
        _parser.Enabled = true;
        Assert.False(PackageInstaller.OwesParserRetry(new PackageManifest { Id = Package }, Registry()));
    }
}
