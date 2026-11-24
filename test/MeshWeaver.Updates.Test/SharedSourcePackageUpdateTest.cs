using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Updates.Test;

/// <summary>
/// Scenario 14 — the PACKAGE UPDATE CHANNEL for a shared-source-only change (#8413). Scenario 13
/// (<see cref="SharedSourceUpdateTest"/>) calls the recompile closure directly; this one drives the
/// installer the registry's auto-update runs: a node-repo package is installed, then updated with a
/// delta that changes ONLY the shared folder <c>{App}/Source</c> (and, in the reported case, the
/// type's own <c>Test</c> folder). The consumer <c>{App}/Run</c> declares
/// <c>shared=@{App}/Source</c>. After the update the record must name a NEW build, and the activation
/// after a dispose must serve it — never "Up to date" over the previous bytes.
/// </summary>
public class SharedSourcePackageUpdateTest(ITestOutputHelper output) : UpdateScenarioBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).AddPluginCatalog();

    private string Package => $"Pkg{Suffix}";
    private string App => $"{Package}/App";
    private string TypePath => $"{App}/Run";
    private string SharedPath => $"{App}/Source/code";

    private PackageManifest Manifest => new()
    {
        Id = Package,
        Name = Package,
        Kind = PackageKind.NodeRepo,
        TargetPartition = Package,
        SourceFolder = Package,
        Version = "1.0.0",
    };

    private static string NodeJson(string id, string ns, string nodeType, object content) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["$type"] = "MeshNode",
            ["id"] = id,
            ["namespace"] = ns,
            ["name"] = id,
            ["nodeType"] = nodeType,
            ["content"] = content,
        });

    private PackageFile Root() => new($"{Package}/index.json",
        NodeJson(Package, "", "Markdown", new Dictionary<string, object>
        {
            ["$type"] = "MarkdownContent",
            ["content"] = "# probe package",
        }));

    private PackageFile AppNode() => new($"{App}.json",
        NodeJson("App", Package, "Markdown", new Dictionary<string, object>
        {
            ["$type"] = "MarkdownContent",
            ["content"] = "# app",
        }));

    private PackageFile Shared(string marker) => new($"{SharedPath}.json",
        NodeJson("code", $"{App}/Source", "Code", new Dictionary<string, object>
        {
            ["$type"] = "CodeConfiguration",
            ["code"] = Code(marker),
            ["language"] = "csharp",
        }));

    private PackageFile TestFile(string note) => new($"{TypePath}/Test/note.json",
        NodeJson("note", $"{TypePath}/Test", "Markdown", new Dictionary<string, object>
        {
            ["$type"] = "MarkdownContent",
            ["content"] = note,
        }));

    private PackageFile TypeNode() => new($"{TypePath}.json",
        NodeJson("Run", App, MeshNode.NodeTypePath, new Dictionary<string, object>
        {
            ["$type"] = "NodeTypeDefinition",
            ["description"] = "Updates-suite probe whose code lives in a SHARED source folder of its package.",
            ["configuration"] = ProbeConfiguration,
            ["sources"] = new[] { "namespace:Source scope:subtree", "namespace:Test scope:subtree", $"shared=@{App}/Source" },
        }));

    private static ModuleManifest ManifestOf(IEnumerable<PackageFile> files, string version) => new()
    {
        ModuleVersion = version,
        Files = files.ToImmutableSortedDictionary(
            f => f.RelativePath, f => $"{version}:{f.RelativePath}", StringComparer.Ordinal),
    };

    private async Task<NodeTypeDefinition> InstallN()
    {
        var files = new[] { Root(), AppNode(), Shared("N"), TestFile("n"), TypeNode() };
        await PackageInstaller.Install(Mesh, Manifest, files, "n")
            .Take(1)
            .Should().Within(Step + Step).Emit("the initial install must complete",
                cancellationToken: TestContext.Current.CancellationToken);
        return await DefinitionWhere(TypePath,
            d => d.CompilationStatus == CompilationStatus.Ok && !string.IsNullOrEmpty(d.LatestAssemblyPath),
            "the installed type must compile from its shared source");
    }

    private async Task AssertRebuilt(NodeTypeDefinition n, string instance, string expectedMarker)
    {
        // The wait IS the assertion: it emits only once the record names a build other than N's.
        await DefinitionWhere(TypePath,
            d => d.CompilationStatus == CompilationStatus.Ok
                 && !d.IsDirty
                 && !string.Equals(d.LatestAssemblyPath, n.LatestAssemblyPath, StringComparison.Ordinal),
            "the update of the shared folder must produce a NEW build of the consumer");
        await Recycle(instance);
        await AssertServes(instance, expectedMarker,
            "after the dispose the build of the updated shared source serves");
    }

    /// <summary>The reported delta: the shared folder AND the type's own Test folder changed.</summary>
    [Fact(Timeout = 300_000)]
    public async Task ADeltaOfTheSharedFolderAndTheTypesTests_RebuildsTheConsumer()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var n = await InstallN();
        var instance = await CreateInstance(TypePath, "inst");
        await AssertServes(instance, "N", "the instance activates on build N");

        var changed = new[] { Shared("N1"), TestFile("n1") };
        var all = new[] { Root(), AppNode(), Shared("N1"), TestFile("n1"), TypeNode() };
        await PackageInstaller.InstallNodeRepoDelta(Mesh, Manifest with { Version = "1.0.1" },
                ManifestOf(all, "v2"), changed, [], "n1")
            .Take(1)
            .Should().Within(Step + Step).Emit("the delta update must complete",
                cancellationToken: TestContext.Current.CancellationToken);

        await AssertRebuilt(n, instance, "N1");
    }

    /// <summary>A delta that changes ONLY the shared folder — no file under the consumer at all.</summary>
    [Fact(Timeout = 300_000)]
    public async Task ADeltaOfOnlyTheSharedFolder_RebuildsTheConsumer()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var n = await InstallN();
        var instance = await CreateInstance(TypePath, "inst");
        await AssertServes(instance, "N", "the instance activates on build N");

        var changed = new[] { Shared("N1") };
        var all = new[] { Root(), AppNode(), Shared("N1"), TestFile("n"), TypeNode() };
        await PackageInstaller.InstallNodeRepoDelta(Mesh, Manifest with { Version = "1.0.1" },
                ManifestOf(all, "v2"), changed, [], "n1")
            .Take(1)
            .Should().Within(Step + Step).Emit("the delta update must complete",
                cancellationToken: TestContext.Current.CancellationToken);

        await AssertRebuilt(n, instance, "N1");
    }

    /// <summary>
    /// #8413 (reopened 2026-11-24): TWO package updates in quick succession — the second only the
    /// shared folder and a test, landing while the first release may still be in flight. The record
    /// must end on a build of the SECOND update with every release request handled.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TwoQuickDeltas_TheSecondTouchingOnlySharedAndTest_EndOnTheSecondsBuild()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var n = await InstallN();
        var instance = await CreateInstance(TypePath, "inst");
        await AssertServes(instance, "N", "the instance activates on build N");

        var all1 = new[] { Root(), AppNode(), Shared("N1"), TestFile("n1"), TypeNode() };
        await PackageInstaller.Install(Mesh, Manifest with { Version = "1.0.1" }, all1, "n1")
            .Take(1)
            .Should().Within(Step + Step).Emit("the first update must complete",
                cancellationToken: TestContext.Current.CancellationToken);

        // No wait for the first release: the second update lands inside it.
        var all2 = new[] { Root(), AppNode(), Shared("N2"), TestFile("n2"), TypeNode() };
        await PackageInstaller.Install(Mesh, Manifest with { Version = "1.0.2" }, all2, "n2")
            .Take(1)
            .Should().Within(Step + Step).Emit("the second update must complete",
                cancellationToken: TestContext.Current.CancellationToken);

        await DefinitionWhere(TypePath,
            d => d.CompilationStatus == CompilationStatus.Ok
                 && !d.IsDirty
                 && !string.Equals(d.LatestAssemblyPath, n.LatestAssemblyPath, StringComparison.Ordinal)
                 && (d.RequestedReleaseAt is null
                     || (d.LastReleaseRequestHandledAt is { } handled && handled >= d.RequestedReleaseAt)),
            "the record must settle on a new build with every release request handled");
        await Recycle(instance);
        await AssertServes(instance, "N2", "after the dispose the build of the SECOND update serves");
    }

    /// <summary>The full re-install of the same change (the fallback the delta takes when it cannot
    /// run) — every package type is released, the consumer among them.</summary>
    [Fact(Timeout = 300_000)]
    public async Task AFullReinstallOfTheSharedFolderChange_RebuildsTheConsumer()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var n = await InstallN();
        var instance = await CreateInstance(TypePath, "inst");
        await AssertServes(instance, "N", "the instance activates on build N");

        var all = new[] { Root(), AppNode(), Shared("N1"), TestFile("n1"), TypeNode() };
        await PackageInstaller.Install(Mesh, Manifest with { Version = "1.0.1" }, all, "n1")
            .Take(1)
            .Should().Within(Step + Step).Emit("the full update must complete",
                cancellationToken: TestContext.Current.CancellationToken);

        await AssertRebuilt(n, instance, "N1");
    }
}
