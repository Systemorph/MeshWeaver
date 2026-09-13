#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Plugin.Build;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>"The generation is newest" and "its types are current" are two facts, and
/// <c>[ModuleLoad]</c> printed only the first</b> (#4158).
///
/// <para>Measured on memex.meshweaver.cloud 2026-09-10 (MeshWeaver.Plugins#1585): the loaded
/// <c>MeshWeaver.AI</c> bundle had the newest generation, the newest <c>written=</c> and types that
/// predated two merged pull requests. <c>mvid=</c> and <c>written=</c> both said "newest" — they
/// are properties of the FILE, and the file was genuinely the newest one on the volume — so the
/// reading "the registry serves stale bytes" was written down and acted on (three RefreshModules,
/// two restarts) before <c>/health</c> falsified it: the bundle had never been ADOPTED
/// (<c>bundle_adoption: … AI: FrameworkDeclined</c>) and the previously adopted build kept serving
/// under the same-MAJOR rule of #3844.</para>
///
/// <para>Nothing on the line separated the two, so this pins that it does: the commit the bytes
/// were BUILT FROM and the framework identity they were built AGAINST both travel to the load
/// line. Both are additive diagnostics — nothing about what an installation RUNS changes.</para>
///
/// <para>🚨 <b>And absence prints as absence.</b> A bundle whose producer recorded no commit
/// prints <c>built-from=(unrecorded)</c> — never the generation, never the version, never the
/// MVID, never a value derived from the path. An invented value here would restate the very defect
/// the line exists to stop: a marker that looks current over bytes that are not.</para>
/// </summary>
public class ModuleLoadLineNamesTheBuildBehindTheBytesTest : IDisposable
{
    private const string Module = "Widget";

    /// <summary>Shaped like the real thing — a CI-stamped framework identity (<c>g&lt;sha&gt;</c>),
    /// which is what every image-pinned pack lane states.</summary>
    private const string Identity = "g7d644de95c1b0a2f3e4d5c6b7a8990112233445";

    /// <summary>The producing repository's commit the bundle was built from.</summary>
    private const string Commit = "1f2e3d4c5b6a798071625344556677889900aabb";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-module-load-line-" + Guid.NewGuid().ToString("N"));

    private readonly string entryDll;

    public ModuleLoadLineNamesTheBuildBehindTheBytesTest()
    {
        var generation = Path.Combine(root, "modules", Module + "@g1");
        Directory.CreateDirectory(generation);
        entryDll = Path.Combine(generation, Module + ".dll");
        // Real, readable PE bytes: the report reads the MVID out of the metadata, and a stand-in
        // would make every assertion below pass for the wrong reason (mvid=unknown).
        File.Copy(typeof(ModuleLoadReport).Assembly.Location, entryDll);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Leaked temp dirs are the OS's to reap; a cleanup error must never mask a failure.
        }
    }

    /// <summary>
    /// The framework identity the bytes were compiled against is already recorded on every landed
    /// entry (<see cref="ModuleActivationEntry.FrameworkMvid"/>) and was simply never printed. It is
    /// the half of #4158 that needs no new data at all.
    /// </summary>
    [Fact]
    public void TheLoadLine_NamesTheFrameworkIdentityTheBytesWereBuiltAgainst()
    {
        var line = Render(new ModuleActivationEntry
        {
            Name = Module,
            Directory = Module + "@g1",
            Version = "1.4.0",
            FrameworkMvid = Identity,
        });

        Assert.Contains("framework=" + Identity, line, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 THE NEGATIVE CONTROL. A module whose activation record carries no commit — every module
    /// landed before the producer recorded one, and every appsettings-baseline module, which has no
    /// record at all — prints an explicit <c>(unrecorded)</c>.
    ///
    /// <para>Asserted by READING THE VALUE BACK off the line rather than by looking for the word:
    /// a "contains (unrecorded)" check would keep passing if the field were also filled with the
    /// generation, the version or a slice of the MVID somewhere else on the line. Absence of
    /// evidence is not evidence, and this is the assertion that holds the implementation to
    /// that.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AModuleWithNoRecordedCommit_SaysSo_RatherThanNamingAnythingItCanSee(bool landed)
    {
        var line = Render(landed
            ? new ModuleActivationEntry
            {
                Name = Module,
                Directory = Module + "@g1",
                Version = "1.4.0",
                FrameworkMvid = Identity,
            }
            : null);

        Assert.Equal("(unrecorded)", FieldOf(line, "built-from"));
    }

    /// <summary>
    /// The consumer-side half of the chain, driven off the PERSISTED record rather than the landing
    /// call: a commit recorded in the activation sidecar reaches the load line.
    ///
    /// <para>🚨 Worth its own test because the sidecar is shared across replicas and across
    /// platform versions — one replica's landing writes the file another replica's boot reads — so
    /// the wire name of the field is a contract, not an implementation detail. A property that
    /// round-trips in memory but is spelled differently on disk would print
    /// <c>(unrecorded)</c> on every OTHER replica, silently.</para>
    /// </summary>
    [Fact]
    public void ACommitRecordedInTheActivationSidecar_ReachesTheLoadLine()
    {
        Directory.CreateDirectory(ModuleActivationSidecar.EntriesDirectory(root));
        File.WriteAllText(
            ModuleActivationSidecar.EntryPath(root, Module),
            $$"""
            {
              "name": "{{Module}}",
              "source": "store",
              "directory": "{{Module}}@g1",
              "version": "1.4.0",
              "frameworkMvid": "{{Identity}}",
              "sourceCommit": "{{Commit}}",
              "enabled": true
            }
            """);

        var entry = Assert.Single(ModuleActivationSidecar.Read(root).Entries);

        Assert.Equal(Commit, FieldOf(Render(entry), "built-from"));
    }

    /// <summary>
    /// 🚨 THE WHOLE CHAIN, through the real pieces: the packer records the commit in the bundle
    /// manifest, <see cref="BundleReader"/> reads it back, the landing service writes it onto the
    /// activation entry, and <see cref="ModuleLoadReport"/> prints it — the hop count is the point,
    /// because the field is useless if any one of them drops it and every one of them is silent
    /// when it does.
    /// </summary>
    [Fact]
    public async Task APackedBundlesCommit_SurvivesPackReadAndLanding_AndReachesTheLoadLine()
    {
        var packDirectory = Path.Combine(root, "pack");
        Directory.CreateDirectory(packDirectory);
        // Real, loadable managed-assembly bytes: the landing measures the module's link
        // requirements (#3538), so a byte stand-in would be refused before anything was recorded.
        File.Copy(typeof(BundleReader).Assembly.Location, Path.Combine(packDirectory, Module + ".dll"));

        var bundles = Path.Combine(root, "bundles");
        var exit = ModulePackCommand.Run(
        [
            packDirectory,
            "--module-name", Module,
            "--plugin", "WidgetPkg",
            "--package-version", "1.4.0",
            "--framework-mvid", Identity,
            "--source-commit", Commit,
            "--out", bundles,
        ]);
        Assert.Equal(0, exit);

        var (manifest, files) = BundleReader.ReadModule(
            File.ReadAllBytes(Path.Combine(bundles, "MeshWeaver.Plugin.WidgetPkg.1.4.0.module.nupkg")));
        Assert.Equal(Commit, manifest!.SourceCommit);

        var deployment = Path.Combine(root, "deployment");
        using var landing = new ModuleLandingService(baseDirectory: deployment);
        await landing.LandModule(
                Module, [.. files.Select(f => (f.FileName, f.Bytes))],
                frameworkMvid: manifest.FrameworkMvid, version: manifest.Version,
                sourceCommit: manifest.SourceCommit)
            .Timeout(TestTimeouts.Convergence).Await();

        var entry = Assert.Single(ModuleActivationSidecar.Read(deployment).Entries);
        var landedDll = Path.Combine(
            ModuleLandingService.ModuleDirectoryFor(deployment, Module, entry), Module + ".dll");

        var lines = ModuleLoadReport.Describe(
            deployment, [(new EffectiveModule(Module + ".dll", entry), landedDll)]);
        var rendered = new StringBuilder();
        ModuleLoadReport.Write(lines, text => rendered.AppendLine(text), text => rendered.AppendLine(text));
        var line = rendered.ToString().Trim();

        Assert.Equal(Commit, FieldOf(line, "built-from"));
        Assert.Equal(Identity, FieldOf(line, "framework"));
    }

    // ───────────────────────────────────────────────────────────── harness

    /// <summary>Renders the ONE <c>[ModuleLoad]</c> info line the boot prints for this module —
    /// through the real <see cref="ModuleLoadReport"/>, never a re-derivation of its format.</summary>
    private string Render(ModuleActivationEntry? landed)
    {
        var lines = ModuleLoadReport.Describe(
            root, [(new EffectiveModule(Module + ".dll", landed), entryDll)]);
        var info = new StringBuilder();
        ModuleLoadReport.Write(
            lines,
            text => info.AppendLine(text),
            _ => Assert.Fail("nothing here is shadowed, so the report must not warn"));
        return info.ToString().Trim();
    }

    /// <summary>
    /// The value of one <c>key=value</c> field of the rendered line. Fails naming the whole line
    /// when the field is absent, so "the field was never printed" can never read as "the field was
    /// printed empty".
    /// </summary>
    private static string FieldOf(string line, string key)
    {
        var at = line.IndexOf(key + "=", StringComparison.Ordinal);
        Assert.True(at >= 0, $"the load line carries no '{key}=' field at all: {line}");
        var tail = line[(at + key.Length + 1)..];
        // Split on the SEPARATOR, never on ')': "(unrecorded)" carries one of its own, and cutting
        // there would read the absence marker back as "(unrecorded" and fail a correct line.
        var separator = tail.IndexOf(", ", StringComparison.Ordinal);
        if (separator >= 0)
            return tail[..separator];
        Assert.EndsWith(")", tail, StringComparison.Ordinal);
        return tail[..^1];
    }
}
