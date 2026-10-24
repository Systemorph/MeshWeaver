#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using MeshWeaver.ComboVerifier;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The combo gate composes the MODULE half of an instance's combo (MeshWeaver#4413).
///
/// <para>🚨 The defect, measured on every combo-verification slot that reached the gate since
/// 2026-10-22 (e.g. run 40002536862, slot 0: 54 packages, 0 passed, 54 failed): the instance's
/// combo names 37 packages with a compiled module (<c>AI</c> → <c>MeshWeaver.AI</c>, …), the
/// verifier materialised only their CONTENT, and the docker argv carried no <c>--module</c>. The
/// tester image ships no module (#2276), so <c>Skill</c>/<c>Agent</c> were never registered:
/// <c>Store</c> failed "NodeType(s) not registered: Skill" and every later package "NodeType
/// 'Store/Plugin' is not registered". A red about the CONTENT, caused by the INSTRUMENT.</para>
///
/// <para>These tests pin the three halves of the fix: the combo's declared modules are READ,
/// the sealed bundles for exactly those are LANDED in the layout the loader probes, and the gate's
/// argv MOUNTS them and passes each as <c>--module</c>.</para>
/// </summary>
public class ComboComposesTheModuleHalfTest
{
    private const string Image = "meshweaver.azurecr.io/memex-portal-ai:3.1.11453";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The shape of a real roll-target combo row (slot 0 of run 40002536862).</summary>
    private static InstanceCombo Combo(params (string Package, string? Module)[] rows) => new()
    {
        ReadAt = DateTimeOffset.Parse("2026-10-23T18:25:37Z"),
        Modules =
        [
            .. rows.Select(r => new ModuleCoordinate
            {
                ModuleId = r.Package,
                Package = new PackageCoordinate
                {
                    RecordPath = $"Plugins/{r.Package}",
                    PackageId = r.Package,
                    CompiledModule = r.Module,
                    SourceName = "Plugins",
                },
            }),
        ],
    };

    [Fact]
    public void DeclaredBy_ReadsEveryInstalledPackagesCompiledModule()
    {
        var combo = Combo(("Store", null), ("AI", "MeshWeaver.AI"), ("Chat", "MeshWeaver.Blazor.Chat"),
            ("Acp", " MeshWeaver.AI.Acp "));

        Assert.Equal(
            new[] { "MeshWeaver.AI", "MeshWeaver.AI.Acp", "MeshWeaver.Blazor.Chat" },
            ComboModuleSet.DeclaredBy(combo).ToArray());
    }

    [Fact]
    public void Compose_LandsExactlyTheDeclaredModules_InTheLayoutTheLoaderProbes()
    {
        var dir = Directory.CreateTempSubdirectory("combo-modules-").FullName;
        try
        {
            var seal = Directory.CreateDirectory(Path.Combine(dir, "sealed", "plugins")).FullName;
            WriteBundle(Path.Combine(seal, "ai.module.nupkg"), "MeshWeaver.AI",
                files: ["MeshWeaver.AI.dll", "MeshWeaver.AI.Closure.dll"],
                assets: ["wwwroot/ai.js"], natives: ["runtimes/linux-x64/native/libx.so"]);
            // Sealed, but this instance does not install it — must NOT be loaded.
            WriteBundle(Path.Combine(seal, "maps.module.nupkg"), "MeshWeaver.Maps",
                files: ["MeshWeaver.Maps.dll"]);

            var composition = ComboModuleSet.Compose(
                ["MeshWeaver.AI", "MeshWeaver.Teams"], [Path.Combine(dir, "sealed")],
                Path.Combine(dir, "out"));

            Assert.Equal(new[] { "MeshWeaver.AI" }, composition.Modules.ToArray());
            Assert.Equal(new[] { "MeshWeaver.Teams" }, composition.NotSealed.ToArray());
            Assert.Equal(new[] { "MeshWeaver.Maps" }, composition.NotDeclared.ToArray());

            var module = Path.Combine(dir, "out", "modules", "MeshWeaver.AI");
            Assert.Equal("MeshWeaver.AI.dll", File.ReadAllText(Path.Combine(module, "MeshWeaver.AI.dll")));
            Assert.True(File.Exists(Path.Combine(module, "MeshWeaver.AI.Closure.dll")));
            Assert.True(File.Exists(Path.Combine(module, "wwwroot", "ai.js")));
            Assert.True(File.Exists(Path.Combine(module, "runtimes", "linux-x64", "native", "libx.so")));
            Assert.False(Directory.Exists(Path.Combine(dir, "out", "modules", "MeshWeaver.Maps")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// 🚨 THE REGRESSION GUARD. The composed modules reach the tester: their root is mounted
    /// read-only BEFORE the image (docker's half) and each module is a <c>--module</c> entry AFTER
    /// the tester's own arguments. Without this the gate runs with no module and reds every package.
    /// </summary>
    [Fact]
    public void RunArgs_MountTheComposedModules_AndPassEachAsModule()
    {
        var composition = new ComboModuleComposition
        {
            Root = "/tmp/combo-work.modules",
            Modules = ["MeshWeaver.AI", "MeshWeaver.Blazor.Chat"],
        };

        var args = DockerImageGate.RunArgs(
            Image, "/tmp/combo-work", "/tmp/combo-host", DockerImageGate.FleetPlatform, composition);

        var image = Array.IndexOf(args, Image);
        var docker = args[..image];
        var mounts = Enumerable.Range(0, docker.Length - 1)
            .Where(i => docker[i] == "-v").Select(i => docker[i + 1]).ToArray();
        Assert.Contains("/tmp/combo-work.modules:/ext:ro", mounts);

        Assert.Equal(
            new[]
            {
                "/host/mw-plugin-test.dll", "/work", "--app", "/app",
                "--report", $"/work/{GateRunReport.FileName}",
                "--module", "/ext/modules/MeshWeaver.AI/MeshWeaver.AI.dll",
                "--module", "/ext/modules/MeshWeaver.Blazor.Chat/MeshWeaver.Blazor.Chat.dll",
            },
            args[(image + 1)..]);
    }

    [Fact]
    public void Compose_RefusesTwoBuildsOfOneModule()
    {
        var dir = Directory.CreateTempSubdirectory("combo-modules-").FullName;
        try
        {
            WriteBundle(Path.Combine(dir, "a.module.nupkg"), "MeshWeaver.AI", files: ["MeshWeaver.AI.dll"]);
            WriteBundle(Path.Combine(dir, "b.module.nupkg"), "MeshWeaver.AI", files: ["MeshWeaver.AI.dll"]);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                ComboModuleSet.Compose(["MeshWeaver.AI"], [dir], Path.Combine(dir, "out")));
            Assert.Contains("carried by two bundles", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Compose_RefusesAnAssetPathThatEscapesTheModuleFolder()
    {
        var dir = Directory.CreateTempSubdirectory("combo-modules-").FullName;
        try
        {
            WriteBundle(Path.Combine(dir, "ai.module.nupkg"), "MeshWeaver.AI",
                files: ["MeshWeaver.AI.dll"], assets: ["../../escape.js"]);

            Assert.Throws<InvalidOperationException>(() =>
                ComboModuleSet.Compose(["MeshWeaver.AI"], [dir], Path.Combine(dir, "out")));
            Assert.False(File.Exists(Path.Combine(dir, "escape.js")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Review finding on #7099: the module NAME comes from the producer's manifest and
    /// becomes a directory. A traversing name must be refused before ANY byte is written — a
    /// per-file check would already have written the files listed before the bad one.</summary>
    [Fact]
    public void Compose_RefusesATraversingModuleName_BeforeWritingAnything()
    {
        var dir = Directory.CreateTempSubdirectory("combo-modules-").FullName;
        try
        {
            var seal = Directory.CreateDirectory(Path.Combine(dir, "a", "b", "seal")).FullName;
            const string evil = "../../escaped-module";
            WriteBundle(Path.Combine(seal, "x.module.nupkg"), evil,
                files: ["payload.dll", evil + ".dll"]);

            Assert.Throws<InvalidOperationException>(() =>
                ComboModuleSet.Compose([evil], [seal], Path.Combine(dir, "a", "b", "out")));
            Assert.Empty(Directory.EnumerateFiles(dir, "payload.dll", SearchOption.AllDirectories));
            Assert.False(Directory.Exists(Path.Combine(dir, "a", "escaped-module")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Review finding on #7099: two DISTINCT entry modules carrying two builds of one
    /// shared sibling are refused, exactly as node-repo-gate.yml refuses them (MeshWeaver#3732);
    /// the same build carried twice is fine.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Compose_RefusesTwoBuildsOfASharedSibling_AcrossDistinctModules(bool diverge)
    {
        var dir = Directory.CreateTempSubdirectory("combo-modules-").FullName;
        try
        {
            WriteBundle(Path.Combine(dir, "ai.module.nupkg"), "MeshWeaver.AI",
                files: ["MeshWeaver.AI.dll", "MeshWeaver.Shared.dll"],
                contents: new Dictionary<string, string> { ["MeshWeaver.Shared.dll"] = "build-A" });
            WriteBundle(Path.Combine(dir, "chat.module.nupkg"), "MeshWeaver.Blazor.Chat",
                files: ["MeshWeaver.Blazor.Chat.dll", "MeshWeaver.Shared.dll"],
                contents: new Dictionary<string, string>
                {
                    ["MeshWeaver.Shared.dll"] = diverge ? "build-B" : "build-A",
                });

            ComboModuleComposition Compose() => ComboModuleSet.Compose(
                ["MeshWeaver.AI", "MeshWeaver.Blazor.Chat"], [dir], Path.Combine(dir, "out"));

            if (diverge)
                Assert.Contains("two different builds",
                    Assert.Throws<InvalidOperationException>(Compose).Message);
            else
                Assert.Equal(2, Compose().Modules.Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Review finding on #7099: an existing but EMPTY bundle directory must not run the
    /// gate with no modules — that is #4413 itself, behind a directory that merely exists.</summary>
    [Fact]
    public void Compose_RefusesBundleDirectoriesThatCarryNoDeclaredModule()
    {
        var dir = Directory.CreateTempSubdirectory("combo-modules-").FullName;
        try
        {
            Assert.Contains("carry none of them",
                Assert.Throws<InvalidOperationException>(() => ComboModuleSet.Compose(
                    ["MeshWeaver.AI"], [dir], Path.Combine(dir, "out"))).Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Review finding on #7099: the composed bytes are the set sealed for the candidate,
    /// which can be newer than what the instance has installed. The divergence is named per
    /// module, never silent.</summary>
    [Fact]
    public void VersionDivergence_NamesEveryModuleComposedAtAnotherVersionThanInstalled()
    {
        var dir = Directory.CreateTempSubdirectory("combo-modules-").FullName;
        try
        {
            WriteBundle(Path.Combine(dir, "ai.module.nupkg"), "MeshWeaver.AI",
                files: ["MeshWeaver.AI.dll"], version: "1.38.0");
            WriteBundle(Path.Combine(dir, "chat.module.nupkg"), "MeshWeaver.Blazor.Chat",
                files: ["MeshWeaver.Blazor.Chat.dll"], version: "1.7.56");
            var combo = Combo(("AI", "MeshWeaver.AI"), ("Chat", "MeshWeaver.Blazor.Chat"));
            combo = combo with
            {
                Modules =
                [
                    .. combo.Modules.Select(m => m with
                    {
                        Package = m.Package! with { Version = m.ModuleId == "AI" ? "1.37.12" : "1.7.56" },
                    }),
                ],
            };

            var composition = ComboModuleSet.Compose(
                ComboModuleSet.DeclaredBy(combo), [dir], Path.Combine(dir, "out"));

            Assert.Equal(
                new[] { "MeshWeaver.AI: installed 1.37.12, composed 1.38.0" },
                composition.VersionDivergence(combo).ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Review finding on #7099 (round 2): the asset and native readers answer EMPTY for a
    /// declared section the archive does not carry, so a bundle missing a declared asset or native
    /// must be refused rather than composed with that section silently dropped.</summary>
    [Theory]
    [InlineData("wwwroot/ai.js")]
    [InlineData("runtimes/linux-x64/native/libx.so")]
    public void Compose_RefusesABundleMissingADeclaredAssetOrNative(string missing)
    {
        var dir = Directory.CreateTempSubdirectory("combo-modules-").FullName;
        try
        {
            WriteBundle(Path.Combine(dir, "ai.module.nupkg"), "MeshWeaver.AI",
                files: ["MeshWeaver.AI.dll"], assets: ["wwwroot/ai.js"],
                natives: ["runtimes/linux-x64/native/libx.so"], omit: [missing]);

            Assert.Contains("incomplete module is refused",
                Assert.Throws<InvalidOperationException>(() => ComboModuleSet.Compose(
                    ["MeshWeaver.AI"], [dir], Path.Combine(dir, "out"))).Message);
            Assert.False(Directory.Exists(Path.Combine(dir, "out", "modules", "MeshWeaver.AI")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Review finding on #7099 (round 2): an install record's <c>Version</c> can be a
    /// commit sha while its <c>ReleasedVersion</c> is the SemVer a bundle states. An unchanged
    /// release is NOT divergent; a legacy record with no released version falls back to
    /// <c>Version</c>.</summary>
    [Theory]
    [InlineData("0c1e61bd8f263a0423ceafcefa04bbfebd930dd6", "1.37.12", "1.37.12", false)]
    [InlineData("0c1e61bd8f263a0423ceafcefa04bbfebd930dd6", "1.37.12", "1.38.0", true)]
    [InlineData("1.37.12", null, "1.37.12", false)]
    [InlineData("1.37.12", null, "1.38.0", true)]
    public void VersionDivergence_ComparesTheReleasedVersion_FallingBackToVersion(
        string version, string? released, string composed, bool divergent)
    {
        var dir = Directory.CreateTempSubdirectory("combo-modules-").FullName;
        try
        {
            WriteBundle(Path.Combine(dir, "ai.module.nupkg"), "MeshWeaver.AI",
                files: ["MeshWeaver.AI.dll"], version: composed);
            var combo = Combo(("AI", "MeshWeaver.AI"));
            combo = combo with
            {
                Modules =
                [
                    .. combo.Modules.Select(m => m with
                    {
                        Package = m.Package! with { Version = version, ReleasedVersion = released },
                    }),
                ],
            };

            var composition = ComboModuleSet.Compose(
                ComboModuleSet.DeclaredBy(combo), [dir], Path.Combine(dir, "out"));

            Assert.Equal(divergent, composition.VersionDivergence(combo).Length == 1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Review finding on #7099 (round 2): a Green deletes the work root AND its module
    /// set beside it; anything else keeps both for inspection.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void WorkRoots_GreenDeletesBoth_AnythingElseKeepsBoth(int exitCode, bool kept)
    {
        var dir = Directory.CreateTempSubdirectory("combo-roots-").FullName;
        try
        {
            var work = Directory.CreateDirectory(Path.Combine(dir, "work")).FullName;
            var modules = Directory.CreateDirectory(Path.Combine(dir, "work.modules", "modules")).Parent!.FullName;
            File.WriteAllText(Path.Combine(modules, "modules", "x.dll"), "x");

            ComboWorkRoots.Settle(exitCode, work, modules, TextWriter.Null);

            Assert.Equal(kept, Directory.Exists(work));
            Assert.Equal(kept, Directory.Exists(modules));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Compose_RefusesAMissingBundleDirectory() =>
        Assert.Throws<InvalidOperationException>(() => ComboModuleSet.Compose(
            ["MeshWeaver.AI"],
            [Path.Combine(Path.GetTempPath(), "combo-absent-" + Guid.NewGuid().ToString("N"))],
            Path.Combine(Path.GetTempPath(), "combo-out-" + Guid.NewGuid().ToString("N"))));

    /// <summary>A module bundle as the pack lane writes it: the manifest declares the module, and
    /// each section carries what it declares (every file's content is its own name).</summary>
    private static void WriteBundle(
        string path, string module, IReadOnlyList<string> files,
        IReadOnlyList<string>? assets = null, IReadOnlyList<string>? natives = null,
        string version = "1.0.0", IReadOnlyDictionary<string, string>? contents = null,
        IReadOnlyCollection<string>? omit = null)
    {
        var manifest = new BundleReader.Manifest(
            Plugin: module, Version: version, FrameworkMvid: null, Assemblies: [],
            Module: new BundleReader.ModuleRef(module, files, StaticAssets: assets)
            {
                NativeAssets = natives,
            });
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        Add(archive, NuGetPackageWriter.ManifestEntry, JsonSerializer.Serialize(manifest, Web));
        foreach (var file in files)
            Add(archive, NuGetPackageWriter.ModuleEntryPathFor(file),
                contents is not null && contents.TryGetValue(file, out var c) ? c : file);
        foreach (var asset in (assets ?? []).Where(a => omit is null || !omit.Contains(a)))
            Add(archive, NuGetPackageWriter.ModuleAssetEntryPathFor(asset), asset);
        foreach (var native in (natives ?? []).Where(n => omit is null || !omit.Contains(n)))
            Add(archive, NuGetPackageWriter.ModuleNativeEntryPathFor(native), native);
    }

    private static void Add(ZipArchive archive, string entry, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
        writer.Write(content);
    }
}
