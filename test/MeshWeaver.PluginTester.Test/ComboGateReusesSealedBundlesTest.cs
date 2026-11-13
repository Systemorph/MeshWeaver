using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MeshWeaver.ComboVerifier;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Plugin.Packaging;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// Pins the combo gate's REUSE of the producing repositories' sealed NodeType bundles
/// (MeshWeaver#4413, owner decision option C, step 1).
///
/// <para><b>The defect.</b> Every slot of every combo run compiled the instance's whole tree from
/// source before gating it — 218 NodeTypes, 9.0 of the 25-minute budget on memex (run
/// 43319782317, job 131538029960: <c>compiled 218 NodeType(s)</c>, 22:34:31 → 22:43:34) — although
/// each of those types is already baked and SEALED for the same framework identity by its producing
/// repository's own lane. The build architecture's rule is content-addressed outputs: an unchanged
/// input is never recompiled.</para>
///
/// <para>The docker CLI is replaced by <see cref="DockerImageGate.DockerOverride"/>. The regression
/// guard (<see cref="ATreeEveryoneSealed_RunsNoBake_AndSeedsTheSealedBundles"/>) fails on the code
/// before this change, which always ran a <c>compile</c> over the whole work root.</para>
/// </summary>
public sealed class ComboGateReusesSealedBundlesTest : IDisposable
{
    private const string Image = "meshweaver.azurecr.io/memex-portal-ai:3.1.13074";
    private const string Identity = "c003e001";
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(25);

    private readonly string scratch = Directory.CreateTempSubdirectory("combo-reuse-").FullName;
    private readonly IoPoolRegistry pools = new();

    private string WorkRoot => Path.Combine(scratch, "slot-2");
    private string SealDir => Path.Combine(scratch, "sealed", "plugins");
    private string HostDir => Path.Combine(scratch, "host");

    /// <summary>A combo of three packages: <c>Lib</c>, <c>App</c> (whose NodeType includes Lib's
    /// sources) and <c>Other</c> (independent).</summary>
    public ComboGateReusesSealedBundlesTest()
    {
        Directory.CreateDirectory(SealDir);
        Directory.CreateDirectory(HostDir);
        WritePackage("Lib", "lib v1");
        WritePackage("App", "app v1", sources: "shared=@Lib/Shared/Source");
        WritePackage("Other", "other v1");
    }

    /// <summary>Releases the pools and deletes the case's scratch tree.</summary>
    public void Dispose()
    {
        pools.Dispose();
        Directory.Delete(scratch, recursive: true);
    }

    /// <summary>A package whose sealed bundle is byte-for-byte this tree's build is reused.</summary>
    [Fact]
    public void ASealedBundleOfTheSameTree_IsReused()
    {
        SealAll();
        var plan = Plan();

        Assert.Equal(["App", "Lib", "Other"], plan.Reused.Keys);
        Assert.Empty(plan.Rebuilt);
        Assert.True(plan.BakesNothing);
    }

    /// <summary>🚨 The instance runs an OLDER tree than the one sealed: its bytes are not the seal's,
    /// so that package is rebuilt — and so is every package whose assembly includes its sources,
    /// while an independent package keeps its seal.</summary>
    [Fact]
    public void ATreeThatDiffersFromItsSeal_IsRebuilt_WithEveryPackageIncludingItsSources()
    {
        SealAll();
        WritePackage("Lib", "lib v0 — the instance has not taken v1 yet");
        var plan = Plan();

        Assert.Equal(["Other"], plan.Reused.Keys);
        Assert.Contains("differs", plan.Rebuilt["Lib"]);
        Assert.Contains("includes sources of 'Lib'", plan.Rebuilt["App"]);
        Assert.Equal(["App", "Lib"], plan.Staged);
    }

    /// <summary>A rebuilt package's install dependencies are staged with it, so its bake resolves —
    /// even when they themselves keep their seal.</summary>
    [Fact]
    public void ARebuiltPackage_StagesWhatItNeeds()
    {
        SealAll();
        WritePackage("App", "app v2", sources: "shared=@Lib/Shared/Source");
        var plan = Plan();

        Assert.Equal(["Lib", "Other"], plan.Reused.Keys);
        Assert.Equal(["App"], plan.Rebuilt.Keys);
        Assert.Equal(["App", "Lib"], plan.Staged);
    }

    /// <summary>A bundle keyed to another framework identity is never reused, and neither is a
    /// package no seal carries.</summary>
    [Fact]
    public void AnotherIdentity_OrNoSeal_IsRebuilt()
    {
        Seal("Lib", identity: "c003e002");
        Seal("App");
        var plan = Plan();

        Assert.Contains("c003e002", plan.Rebuilt["Lib"]);
        Assert.Contains("no fetched seal", plan.Rebuilt["Other"]);
        Assert.Contains("includes sources of 'Lib'", plan.Rebuilt["App"]);
        Assert.Empty(plan.Reused);
    }

    /// <summary>An unreadable seal file reuses nothing, and is NAMED rather than skipped.</summary>
    [Fact]
    public void AnUnreadableSeal_IsNamed()
    {
        SealAll();
        File.WriteAllText(Path.Combine(SealDir, "Broken.zip"), "not a zip");
        var plan = Plan();

        Assert.Contains(plan.SealProblems, p => p.Contains("Broken.zip", StringComparison.Ordinal));
        Assert.Equal(3, plan.Reused.Count);
    }

    /// <summary>🚨 THE REGRESSION GUARD: when every package's sealed bundle is this tree's build,
    /// no compile runs, and the gate consumes the sealed bundles as its seed.</summary>
    [Fact]
    public void ATreeEveryoneSealed_RunsNoBake_AndSeedsTheSealedBundles()
    {
        SealAll();
        var docker = new FakeDocker();
        var run = Gate(docker).Execute(Image, WorkRoot, CancellationToken.None);

        Assert.Null(run.Error);
        var container = Assert.Single(docker.Containers);
        Assert.DoesNotContain("compile", container);
        Assert.Equal("--seed", container[^2]);

        var seed = DockerImageGate.BakeRootFor(WorkRoot);
        Assert.Equal(Identity, File.ReadAllText(Path.Combine(seed, DockerImageGate.FrameworkMvidFile)));
        Assert.Equal(
            ["App", "Lib", "Other"],
            Directory.EnumerateFiles(seed, "*.zip").Select(f => BundleReader.ReadManifest(f)!.Plugin)
                .OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>When a package is rebuilt, the bake mounts ONLY the staged packages — never the
    /// whole work root — and the seed is that bake plus the reused bundles it did not produce.</summary>
    [Fact]
    public void ARebuiltPackage_IsBakedAlone_AndTheSeedCombinesBothHalves()
    {
        SealAll();
        WritePackage("Other", "other v2");
        var docker = new FakeDocker(onCompile: (work, bake) =>
        {
            Assert.Equal(["Other"], Directory.EnumerateDirectories(work).Select(Path.GetFileName));
            File.WriteAllText(Path.Combine(bake, DockerImageGate.FrameworkMvidFile), Identity);
            WriteBundle(Path.Combine(bake, "Other.zip"), "Other", Identity, "other v2");
        });
        var run = Gate(docker).Execute(Image, WorkRoot, CancellationToken.None);

        Assert.Null(run.Error);
        Assert.Equal(2, docker.Containers.Count);
        Assert.Contains($"{SealedBundleReuse.StageRootFor(WorkRoot)}:/work", docker.Containers[0]);
        var seed = DockerImageGate.BakeRootFor(WorkRoot);
        Assert.Equal(
            ["App", "Lib", "Other"],
            Directory.EnumerateFiles(seed, "*.zip").Select(f => BundleReader.ReadManifest(f)!.Plugin)
                .OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>A bake keyed to another identity than the reused bundles cannot be one seed.</summary>
    [Fact]
    public void ABakeOfAnotherIdentity_IsAnError()
    {
        SealAll();
        WritePackage("Other", "other v2");
        var docker = new FakeDocker(onCompile: (_, bake) =>
        {
            File.WriteAllText(Path.Combine(bake, DockerImageGate.FrameworkMvidFile), "c003e009");
            WriteBundle(Path.Combine(bake, "Other.zip"), "Other", "c003e009", "other v2");
        });
        var run = Gate(docker).Execute(Image, WorkRoot, CancellationToken.None);

        Assert.Contains("c003e009", run.Error);
        Assert.Single(docker.Containers);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private SealedBundleSource Source => new([SealDir], Identity);

    private SealedBundlePlan Plan() => SealedBundleReuse.Plan(WorkRoot, Source);

    private DockerImageGate Gate(FakeDocker docker) =>
        new(pools, Budget, TextWriter.Null, DockerImageGate.FleetPlatform, HostDir,
            modules: null, sealedBundles: Source)
        {
            DockerOverride = docker.Call,
        };

    private void WritePackage(string id, string text, string? sources = null)
    {
        var root = Path.Combine(WorkRoot, id);
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(Path.Combine(root, "Shared", "Source"));
        foreach (var (path, content) in PackageFiles(id, text, sources))
            File.WriteAllText(Path.Combine(root, path), content);
    }

    private static IEnumerable<(string Path, string Content)> PackageFiles(
        string id, string text, string? sources)
    {
        yield return ("index.json", $$"""{"id":"{{id}}","name":"{{id}}","nodeType":"Markdown"}""");
        yield return (Path.Combine("Shared", "Source", "Code.cs"), $"// {text}");
        var sourceList = sources is null ? "" : $$""","sources":["{{sources}}"]""";
        yield return ("Thing.json",
            "{\"id\":\"Thing\",\"nodeType\":\"NodeType\",\"content\":{\"$type\":\"NodeTypeDefinition\""
            + sourceList + "}}");
    }

    private void SealAll()
    {
        Seal("Lib");
        Seal("App");
        Seal("Other");
    }

    /// <summary>Seals <paramref name="id"/>'s CURRENT tree — what its producing lane baked.</summary>
    private void Seal(string id, string identity = Identity)
    {
        var root = Path.Combine(WorkRoot, id);
        var content = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => (Relative: Path.GetRelativePath(root, f).Replace('\\', '/'), Bytes: File.ReadAllBytes(f)))
            .OrderBy(f => f.Relative, StringComparer.Ordinal)
            .ToList();
        using var file = File.Create(Path.Combine(SealDir, id + ".zip"));
        BundleWriter.Write(file, id, "1.0.0", identity,
            [new BundleWriter.AssemblyEntry($"{id}/Thing", () => new MemoryStream([1, 2, 3]))],
            "4b97be19",
            [.. content.Select(c => new BundleWriter.ContentEntry(c.Relative, () => new MemoryStream(c.Bytes)))],
            sourceIncluded: true, producerPlatformVersion: "3.1.13074");
    }

    private static void WriteBundle(string path, string id, string identity, string text)
    {
        using var file = File.Create(path);
        BundleWriter.Write(file, id, "1.0.0", identity,
            [new BundleWriter.AssemblyEntry($"{id}/Thing", () => new MemoryStream([4, 5, 6]))],
            "4b97be19",
            [new BundleWriter.ContentEntry("index.json", () => new MemoryStream(Encoding.UTF8.GetBytes(text)))],
            sourceIncluded: true, producerPlatformVersion: "3.1.13074");
    }

    /// <summary>Answers the inspect calls as present for the fleet platform and records every
    /// <c>docker run</c>; a <c>compile</c> container runs <paramref name="onCompile"/> against the
    /// host side of its <c>/work</c> and <c>/bake</c> mounts.</summary>
    private sealed class FakeDocker(Action<string, string>? onCompile = null)
    {
        public List<ImmutableArray<string>> Containers { get; } = [];

        public DockerImageGate.DockerResult Call(
            IReadOnlyList<string> args, CancellationToken ct, TimeSpan budget)
        {
            if (args[0] == "image")
                return new DockerImageGate.DockerResult(0,
                    args.Contains("{{.Os}}/{{.Architecture}}") ? DockerImageGate.FleetPlatform : "sha256:x",
                    TimedOut: false);
            Containers.Add([.. args]);
            if (!args.Contains("compile"))
                return new DockerImageGate.DockerResult(0, "gate ran", TimedOut: false);
            var mounts = Enumerable.Range(0, args.Count - 1).Where(i => args[i] == "-v")
                .Select(i => args[i + 1]).ToList();
            var work = mounts.Single(m => m.EndsWith(":/work", StringComparison.Ordinal))[..^":/work".Length];
            var bake = mounts.Single(m => m.EndsWith(":/bake", StringComparison.Ordinal))[..^":/bake".Length];
            (onCompile ?? throw new InvalidOperationException("no compile expected"))(work, bake);
            return new DockerImageGate.DockerResult(0, "baked", TimedOut: false);
        }
    }
}
