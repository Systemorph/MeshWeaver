#pragma warning disable CS1591

using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.Plugin.Build;
using MeshWeaver.Plugin.Packaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The <c>module-pack</c> CLI (#1664 Slice B): its inputs flow into FILE PATHS (the entry-DLL
/// probe, the closure entries, the output bundle name), so path-injection-shaped values must be a
/// clear exit-2 refusal — and what it packs must read back through the ONE reader
/// (<see cref="BundleReader.ReadModule"/>) the consumers use.
/// </summary>
public class ModulePackCommandTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-module-pack-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The built-against framework identity every pack must state (#3211) — shaped like the real
    /// thing (<c>s&lt;hash&gt;</c> from a surface manifest, <c>g&lt;sha&gt;</c> from a CI stamp).
    /// </summary>
    private const string Identity = "s0f1e2d3c4b5a697";

    public ModulePackCommandTest()
    {
        Directory.CreateDirectory(Path.Combine(root, "closure"));
        File.WriteAllBytes(Path.Combine(root, "closure", "Widget.dll"), "WIDGET"u8.ToArray());
    }

    public void Dispose()
    {
        // Best-effort temp cleanup — must run on assertion failure too, never mask the failure.
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Leaked temp dirs are the OS's to reap; a cleanup error must not fail the test.
        }
    }

    [Theory]
    [InlineData("--module-name", "../evil")]
    [InlineData("--module-name", "a/b")]
    [InlineData("--plugin", "..\\..\\escape")]
    [InlineData("--package-version", "1.0.0/../../x")]
    public void PathInjectionShapedInputs_AreRejected_BeforeAnyPathIsComposed(
        string option, string value)
    {
        // 🚨 The Copilot finding: these values reach Path.Combine (entry-DLL probe, bundle file
        // name), so a traversal-shaped value must be a clear refusal naming it — never a probe
        // outside the module folder or a bundle written somewhere surprising.
        var args = new List<string>
        {
            Path.Combine(root, "closure"),
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.0.0",
            "--framework-mvid", Identity,
            "--out", Path.Combine(root, "out"),
        };
        args[args.IndexOf(option) + 1] = value;

        var exit = ModulePackCommand.Run([.. args]);

        Assert.Equal(2, exit);
        Assert.False(Directory.Exists(Path.Combine(root, "out")),
            "a refused invocation must not have written anything");
    }

    /// <summary>
    /// 🚨 THE NEGATIVE CONTROL for #3211 — a bundle that cannot state what it was built against is
    /// not written at all.
    ///
    /// <para>This is the exact invocation that packed all 34 of MeshWeaver.Plugins' bundles on
    /// 2026-09-03 (run 33773265959): no <c>--framework-mvid</c>, and no
    /// <c>MeshWeaver.Compiler.dll</c> beside the module — because on both the sdk and the container
    /// path the platform is the IMAGE, so the anchor is in the extracted <c>/app</c>, never in the
    /// module's output. It used to print a warning and omit the field, which shipped #3154's
    /// (version, identity) comparison with nothing to compare on every installation in the
    /// fleet.</para>
    /// </summary>
    [Fact]
    public void NoStatedIdentity_AndNoAnchorBesideTheModule_IsRefused_AndWritesNothing()
    {
        var outDir = Path.Combine(root, "out-no-identity");
        var exit = ModulePackCommand.Run(
        [
            Path.Combine(root, "closure"),
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.2.0",
            "--min-mesh-version", "3.0.0",
            "--out", outDir,
        ]);

        Assert.Equal(2, exit);
        Assert.False(Directory.Exists(outDir),
            "a bundle that states no framework identity must not exist at all — it is refused "
            + "where it is created, not shelved and skipped forever downstream");
    }

    /// <summary>A stated identity that is only whitespace reads as UNSTATED downstream
    /// (<c>ModuleUpdateDecision</c> treats blank as unknown on both sides), so it is refused here
    /// rather than written as a value that means nothing.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankStatedIdentity_IsRefused(string blank)
    {
        var outDir = Path.Combine(root, "out-blank-identity");
        var exit = ModulePackCommand.Run(
        [
            Path.Combine(root, "closure"),
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.2.0",
            "--framework-mvid", blank,
            "--out", outDir,
        ]);

        Assert.Equal(2, exit);
        Assert.False(Directory.Exists(outDir));
    }

    /// <summary>The other half of the pair: with the anchor assembly NAMED, the packer reads its
    /// identity itself — the shape the lane uses (<c>--graph-dll &lt;platform /app&gt;</c>).</summary>
    [Fact]
    public void TheAnchorAssembly_WhenNamed_StatesTheIdentityWithoutTheFlag()
    {
        // Any real PE works: FrameworkIdentity.ReadIdentity resolves the stamped identity when the
        // assembly carries one and the MVID otherwise — the same resolution the runtime compares.
        var anchor = typeof(ModulePackCommand).Assembly.Location;
        var expected = FrameworkIdentity.ReadIdentity(anchor);

        var outDir = Path.Combine(root, "out-anchor");
        var exit = ModulePackCommand.Run(
        [
            Path.Combine(root, "closure"),
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.7.0",
            "--graph-dll", anchor,
            "--out", outDir,
        ]);

        Assert.Equal(0, exit);
        var (manifest, _) = BundleReader.ReadModule(File.ReadAllBytes(
            Path.Combine(outDir, "MeshWeaver.Plugin.WidgetPkg.1.7.0.module.nupkg")));
        Assert.False(string.IsNullOrWhiteSpace(expected));
        Assert.Equal(expected, manifest!.FrameworkMvid);
    }

    /// <summary>
    /// 🚨 #3176 — the anchor is a property of the PLATFORM, so an anchor inside the module directory
    /// being packed is refused, even though it is a real, readable PE.
    ///
    /// <para><b>Why a module-local anchor is never right.</b> A copy of
    /// <c>MeshWeaver.Compiler.dll</c> beside the module exists only when that module's reference
    /// closure happens to reach it — in core only through <c>MeshWeaver.Graph</c> or
    /// <c>MeshWeaver.Compiler.Pipeline</c> — so the anchor's very EXISTENCE is an accident of what
    /// the module imports. Measured on core CD run 33874892203: <c>MeshWeaver.AI</c> and
    /// <c>MeshWeaver.Markdown.Collaboration</c> reach it and packed green; <c>MeshWeaver.Maps</c>
    /// (→ <c>MeshWeaver.Layout</c>) and <c>MeshWeaver.Payments.Stripe</c>
    /// (→ <c>MeshWeaver.Mesh.Contract</c>) do not and packed RED, skipping the seal.</para>
    ///
    /// <para>And where it IS reached it is a REBUILD carrying that module's
    /// <c>-p:Version</c>, which moves its MVID — so the same run's two green bundles stated two
    /// different framework identities (<c>be27d0fb…</c> vs <c>d756b82e…</c>) for one platform. Either
    /// way the bundle states an identity no consumer can ever match, which is exactly the blind spot
    /// #3211 closed at the producer and #3154 depends on.</para>
    /// </summary>
    [Fact]
    public void AnAnchorInsideTheModuleDirectory_IsRefused_AndWritesNothing()
    {
        // A REAL assembly, so this cannot pass for "the file was not readable" — the refusal is
        // about WHERE the anchor is, not whether it parses. Copied beside the module exactly as a
        // publish of a module whose closure reaches the identity assembly would leave it.
        var moduleDirectory = Path.Combine(root, "closure");
        var moduleLocalAnchor =
            Path.Combine(moduleDirectory, FrameworkIdentity.IdentityAssembly + ".dll");
        File.Copy(typeof(ModulePackCommand).Assembly.Location, moduleLocalAnchor);
        Assert.False(string.IsNullOrWhiteSpace(FrameworkIdentity.ReadIdentity(moduleLocalAnchor)),
            "the fixture's anchor must be a readable PE, or this test would pass for the wrong reason");

        var outDir = Path.Combine(root, "out-module-local-anchor");
        var exit = ModulePackCommand.Run(
        [
            moduleDirectory,
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.7.0",
            "--graph-dll", moduleLocalAnchor,
            "--out", outDir,
        ]);

        Assert.Equal(2, exit);
        Assert.False(Directory.Exists(outDir),
            "a bundle whose identity was read off the module's own output must not exist at all — "
            + "the identity it would state names no platform build a consumer can have landed");
    }

    /// <summary>
    /// 🚨 Containment, not directory equality (Copilot review on #3306). The real CD path was
    /// NESTED — `…/src/MeshWeaver.Maps/bin/Release/net10.0/publish/MeshWeaver.Compiler.dll` — so an
    /// anchor deeper inside the module tree is the shape this guard most needs to catch. A check
    /// that compared the anchor's immediate directory against the module directory accepted this
    /// case, which would have let a per-module rebuilt identity through exactly where it lives.
    /// </summary>
    [Fact]
    public void AnAnchorNestedDeeperInsideTheModuleTree_IsAlsoRefused()
    {
        var moduleDirectory = Path.Combine(root, "closure");
        var nested = Path.Combine(moduleDirectory, "bin", "Release", "net10.0", "publish");
        Directory.CreateDirectory(nested);
        var nestedAnchor = Path.Combine(nested, FrameworkIdentity.IdentityAssembly + ".dll");
        File.Copy(typeof(ModulePackCommand).Assembly.Location, nestedAnchor);

        var outDir = Path.Combine(root, "out-nested-anchor");
        var exit = ModulePackCommand.Run(
        [
            moduleDirectory,
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.7.0",
            "--graph-dll", nestedAnchor,
            "--out", outDir,
        ]);

        Assert.Equal(2, exit);
        Assert.False(Directory.Exists(outDir));
    }

    /// <summary>A SIBLING directory whose name merely starts with the module directory's name is
    /// NOT inside it, and must still pack — the containment check above is a path-prefix test, and
    /// without the trailing separator "…/closure-refs" would read as inside "…/closure".</summary>
    [Fact]
    public void AnAnchorInASiblingDirectoryWithAPrefixName_IsAccepted()
    {
        var siblingRefs = Path.Combine(root, "closure-refs");
        Directory.CreateDirectory(siblingRefs);
        var anchor = Path.Combine(siblingRefs, FrameworkIdentity.IdentityAssembly + ".dll");
        File.Copy(typeof(ModulePackCommand).Assembly.Location, anchor);
        var expected = FrameworkIdentity.ReadIdentity(anchor);

        var outDir = Path.Combine(root, "out-sibling-anchor");
        var exit = ModulePackCommand.Run(
        [
            Path.Combine(root, "closure"),
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.7.0",
            "--graph-dll", anchor,
            "--out", outDir,
        ]);

        Assert.Equal(0, exit);
        var (manifest, _) = BundleReader.ReadModule(File.ReadAllBytes(
            Path.Combine(outDir, "MeshWeaver.Plugin.WidgetPkg.1.7.0.module.nupkg")));
        Assert.Equal(expected, manifest!.FrameworkMvid);
    }

    /// <summary>The same refusal when the anchor is not named at all and the packer's DEFAULT probe
    /// finds a module-local copy. This is the silent arm: without the guard the probe reads it and
    /// the bundle packs GREEN stating a per-module identity, which is how two identities for one
    /// platform reached a CD run unnoticed.</summary>
    [Fact]
    public void TheDefaultProbe_FindingAModuleLocalAnchor_IsRefused_RatherThanReadingIt()
    {
        var moduleDirectory = Path.Combine(root, "closure");
        File.Copy(
            typeof(ModulePackCommand).Assembly.Location,
            Path.Combine(moduleDirectory, FrameworkIdentity.IdentityAssembly + ".dll"));

        var outDir = Path.Combine(root, "out-default-probe-anchor");
        var exit = ModulePackCommand.Run(
        [
            moduleDirectory,
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.7.0",
            "--out", outDir,
        ]);

        Assert.Equal(2, exit);
        Assert.False(Directory.Exists(outDir));
    }

    /// <summary>
    /// 🚨 #4158 — the bundle states the PRODUCING REPOSITORY'S COMMIT its bytes were built from,
    /// beside the framework identity they were built against, so a consumer's <c>[ModuleLoad]</c>
    /// line can tell "this generation is the newest" from "its types are current". On
    /// memex.meshweaver.cloud 2026-09-10 (MeshWeaver.Plugins#1585) those two were read as one fact:
    /// <c>mvid=</c> and <c>written=</c> are both properties of the FILE, the file WAS the newest on
    /// the volume, and its types predated two merged pull requests.
    ///
    /// <para>Paired with its own negative: a pack that states no commit writes NO field, so a
    /// consumer reading an older bundle and a consumer reading this one answer the same thing —
    /// "nobody recorded it" — rather than one of them reading an empty string.</para>
    /// </summary>
    [Theory]
    [InlineData("6a5f0c1d2e3b4a596877665544332211aabbccdd")]
    [InlineData(null)]
    public void TheBuiltFromCommit_IsRecordedWhenStated_AndAbsentWhenNot(string? commit)
    {
        var outDir = Path.Combine(root, "out-source-commit-" + (commit ?? "none"));
        var args = new List<string>
        {
            Path.Combine(root, "closure"),
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.5.0",
            "--framework-mvid", Identity,
            "--out", outDir,
        };
        if (commit is not null)
            args.AddRange(["--source-commit", commit]);

        Assert.Equal(0, ModulePackCommand.Run([.. args]));

        var (manifest, _) = BundleReader.ReadModule(File.ReadAllBytes(
            Path.Combine(outDir, "MeshWeaver.Plugin.WidgetPkg.1.5.0.module.nupkg")));

        Assert.Equal(commit, manifest!.SourceCommit);
        // The identity must be untouched by the new field — the two are separate statements, and a
        // pack that started answering one with the other would be this issue's defect inverted.
        Assert.Equal(Identity, manifest.FrameworkMvid);
    }

    /// <summary>
    /// A commit that reads as ABSENT downstream is refused where it is created. Blank, padded or
    /// shaped like anything but a revision identifier, it would print as an empty field on every
    /// consumer's load line instead of the <c>(unrecorded)</c> that says nobody stated one — the
    /// same rule the blank framework identity follows, for the same reason.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a b")]
    [InlineData("../escape")]
    public void ASourceCommitThatWouldReadAsAbsent_IsRefused(string bad)
    {
        var outDir = Path.Combine(root, "out-bad-source-commit");
        var exit = ModulePackCommand.Run(
        [
            Path.Combine(root, "closure"),
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.5.0",
            "--framework-mvid", Identity,
            "--source-commit", bad,
            "--out", outDir,
        ]);

        Assert.Equal(2, exit);
        Assert.False(Directory.Exists(outDir),
            "a refused invocation must not have written anything");
    }

    [Fact]
    public void APackedModuleBundle_RoundTripsThroughTheReader()
    {
        var outDir = Path.Combine(root, "out");
        var exit = ModulePackCommand.Run(
        [
            Path.Combine(root, "closure"),
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.2.0",
            "--framework-mvid", Identity,
            "--min-mesh-version", "3.0.0",
            "--out", outDir,
        ]);

        Assert.Equal(0, exit);
        var bundlePath = Path.Combine(outDir, "MeshWeaver.Plugin.WidgetPkg.1.2.0.module.nupkg");
        Assert.True(File.Exists(bundlePath));

        var (manifest, files) = BundleReader.ReadModule(File.ReadAllBytes(bundlePath));

        Assert.Equal("WidgetPkg", manifest!.Plugin);
        Assert.Equal("1.2.0", manifest.Version);
        Assert.Equal("Widget", manifest.Module!.AssemblyName);
        Assert.Equal("3.0.0", manifest.Module.MinMeshVersion);
        // 🚨 #3211: the bundle states what it was built against, always. Every consumer's update
        // decision compares this against what it has landed (#3154), so an omitted value is not a
        // missing nicety — it is a permanent "up to date, identity could not be checked".
        Assert.Equal(Identity, manifest.FrameworkMvid);
        var only = Assert.Single(files);
        Assert.Equal("Widget.dll", only.FileName);
        Assert.Equal("WIDGET", Encoding.UTF8.GetString(only.Bytes));
    }

    [Fact]
    public void DepsClosure_BundlesTheModulesOwnDependencies_AndReadsBack()
    {
        // The build output the flag expects: entry DLL + its private dependency (copied there by
        // CopyLocalLockFileAssemblies=true) + the SDK's deps.json declaring the split.
        File.WriteAllBytes(Path.Combine(root, "closure", "Gadget.Sdk.dll"), "SDK"u8.ToArray());
        File.WriteAllText(Path.Combine(root, "closure", "Widget.deps.json"), """
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
              "targets": {
                ".NETCoreApp,Version=v10.0": {
                  "Widget/1.0.0": {
                    "dependencies": { "MeshWeaver.AI": "3.0.0", "Gadget.Sdk": "2.0.0" },
                    "runtime": { "Widget.dll": {} }
                  },
                  "MeshWeaver.AI/3.0.0": { "runtime": { "MeshWeaver.AI.dll": {} } },
                  "Gadget.Sdk/2.0.0": { "runtime": { "lib/net10.0/Gadget.Sdk.dll": {} } }
                }
              },
              "libraries": {
                "Widget/1.0.0": { "type": "project" },
                "MeshWeaver.AI/3.0.0": { "type": "project" },
                "Gadget.Sdk/2.0.0": { "type": "package" }
              }
            }
            """);

        var outDir = Path.Combine(root, "out-deps");
        var exit = ModulePackCommand.Run(
        [
            Path.Combine(root, "closure"),
            "--deps-closure",
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.3.0",
            "--framework-mvid", Identity,
            "--out", outDir,
        ]);

        Assert.Equal(0, exit);
        var (manifest, files) = BundleReader.ReadModule(File.ReadAllBytes(
            Path.Combine(outDir, "MeshWeaver.Plugin.WidgetPkg.1.3.0.module.nupkg")));

        // The private dependency rides in the bundle AND in the manifest's declared closure;
        // the platform side does not.
        Assert.Contains("Gadget.Sdk.dll", manifest!.Module!.Assemblies!);
        Assert.DoesNotContain("MeshWeaver.AI.dll", manifest.Module.Assemblies!);
        Assert.Contains(files, f => f.FileName == "Gadget.Sdk.dll"
                                    && Encoding.UTF8.GetString(f.Bytes) == "SDK");
    }

    /// <summary>
    /// 🚨 #4126 — a module's RID-specific NATIVE payload rides its bundle, at the path the runtime
    /// resolver probes. Measured on <c>MeshWeaver.AppleMessages</c>, which needs
    /// <c>libe_sqlite3</c>: before this the derivation warned and dropped it, the bundle format had
    /// no section for it, and the module worked only where the HOST happened to ship the engine —
    /// the same "the host happens to have it" shape the 2026-09-01 rule called a defect for managed
    /// assemblies.
    ///
    /// <para>The round trip is the assertion: pack it, read it back, and check the relative path is
    /// preserved EXACTLY — it is not decoration, it is the layout <c>ModuleNativeAssets</c> probes
    /// (<c>&lt;moduleDir&gt;/runtimes/&lt;rid&gt;/native/&lt;lib&gt;</c>).</para>
    ///
    /// <para>Two negative controls travel with it. The native must NOT appear in the flat
    /// <c>module.assemblies</c> list — every consumer of <c>meshweaver/modules</c> filters to
    /// entries with no <c>/</c> in the remainder, so a native declared there is silently skipped
    /// rather than laid out. And a bundle with NO native must declare no section at all, so a
    /// bundle from before this existed and one that simply has none read the same.</para>
    /// </summary>
    [Fact]
    public void DepsClosure_CarriesANativePayload_AtThePathTheLoaderProbes()
    {
        const string nativeRelative = "runtimes/linux-x64/native/libe_sqlite3.so";
        var nativeDir = Path.Combine(root, "closure", "runtimes", "linux-x64", "native");
        Directory.CreateDirectory(nativeDir);
        File.WriteAllBytes(Path.Combine(nativeDir, "libe_sqlite3.so"), "ENGINE"u8.ToArray());
        File.WriteAllText(Path.Combine(root, "closure", "Widget.deps.json"), """
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
              "targets": {
                ".NETCoreApp,Version=v10.0": {
                  "Widget/1.0.0": {
                    "dependencies": { "SQLitePCLRaw.lib.e_sqlite3": "3.53.3" },
                    "runtime": { "Widget.dll": {} }
                  },
                  "SQLitePCLRaw.lib.e_sqlite3/3.53.3": {
                    "runtimeTargets": {
                      "runtimes/linux-x64/native/libe_sqlite3.so": { "rid": "linux-x64", "assetType": "native" }
                    }
                  }
                }
              },
              "libraries": {
                "Widget/1.0.0": { "type": "project" },
                "SQLitePCLRaw.lib.e_sqlite3/3.53.3": { "type": "package" }
              }
            }
            """);

        var outDir = Path.Combine(root, "out-native");
        Assert.Equal(0, ModulePackCommand.Run(
        [
            Path.Combine(root, "closure"),
            "--deps-closure",
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.7.0",
            "--framework-mvid", Identity,
            "--out", outDir,
        ]));

        var bundle = File.ReadAllBytes(
            Path.Combine(outDir, "MeshWeaver.Plugin.WidgetPkg.1.7.0.module.nupkg"));
        var (manifest, files) = BundleReader.ReadModule(bundle);

        Assert.Equal([nativeRelative], manifest!.Module!.NativeAssets);
        var native = Assert.Single(BundleReader.ReadModuleNativeAssets(bundle));
        Assert.Equal(nativeRelative, native.RelativePath);
        Assert.Equal("ENGINE", Encoding.UTF8.GetString(native.Bytes));

        // ── negative control 1: it is NOT in the flat closure, which is filtered to entries with
        // no '/' at every consumer — a native declared there would be silently skipped.
        Assert.DoesNotContain(manifest.Module.Assemblies!,
            a => a.Contains('/') || a.EndsWith(".so", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.FileName.Contains('/'));
    }

    /// <summary>
    /// 🚨 #4126 — the CONTAINER lane's route into the same section. That lane passes NO
    /// <c>--deps-closure</c>, because <c>build-project</c> compiles through Roslyn directly and
    /// there is no SDK deps.json to derive from: its builder derives the payloads from the IMAGE's
    /// and the shelf's deps.json, lays them out under the pack input, and names them in
    /// <c>module-natives.txt</c>. <c>--with-native</c> is where that provenance becomes a bundle
    /// section.
    ///
    /// <para>🚨 It takes the opposite argument to <c>--with</c>, and deliberately: <c>--with</c>
    /// REFUSES a path component because the flat closure has no place for one, while this REQUIRES
    /// the path because the path is what the loader probes. A lane that fed one to the other would
    /// be wrong whichever way it was written — hence two flags and two manifests.</para>
    /// </summary>
    [Fact]
    public void WithNative_CarriesADeclaredPayload_OnALaneWithNoDepsJson()
    {
        const string nativeRelative = "runtimes/linux-x64/native/libe_sqlite3.so";
        var nativeDir = Path.Combine(root, "closure", "runtimes", "linux-x64", "native");
        Directory.CreateDirectory(nativeDir);
        File.WriteAllBytes(Path.Combine(nativeDir, "libe_sqlite3.so"), "ENGINE"u8.ToArray());

        var outDir = Path.Combine(root, "out-with-native");
        Assert.Equal(0, ModulePackCommand.Run(
        [
            Path.Combine(root, "closure"),
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.9.0",
            "--framework-mvid", Identity,
            "--with-native", nativeRelative,
            "--out", outDir,
        ]));

        var bundle = File.ReadAllBytes(
            Path.Combine(outDir, "MeshWeaver.Plugin.WidgetPkg.1.9.0.module.nupkg"));
        var (manifest, _) = BundleReader.ReadModule(bundle);
        Assert.Equal([nativeRelative], manifest!.Module!.NativeAssets);
        Assert.Equal("ENGINE",
            Encoding.UTF8.GetString(Assert.Single(BundleReader.ReadModuleNativeAssets(bundle)).Bytes));
    }

    /// <summary>
    /// A payload at a layout the loader never probes is REFUSED at the pack, not carried. Bytes at
    /// such a path read as shipped and behave as absent, and the packer is the last place that can
    /// still say so to the person who can fix it.
    /// </summary>
    [Fact]
    public void WithNative_RefusesAPathTheLoaderWouldNeverProbe()
    {
        var nativeDir = Path.Combine(root, "closure", "runtimes", "linux-x64", "other", "native");
        Directory.CreateDirectory(nativeDir);
        File.WriteAllBytes(Path.Combine(nativeDir, "libe_sqlite3.so"), "ENGINE"u8.ToArray());

        Assert.Equal(2, ModulePackCommand.Run(
        [
            Path.Combine(root, "closure"),
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.9.1",
            "--framework-mvid", Identity,
            "--with-native", "runtimes/linux-x64/other/native/libe_sqlite3.so",
            "--out", Path.Combine(root, "out-bad-native"),
        ]));
    }

    /// <summary>
    /// A DECLARED payload the pack input does not carry is refused too — packing a module that
    /// declares an engine it does not ship lands a module that throws at its first P/Invoke, which
    /// is the failure the declaration exists to prevent.
    /// </summary>
    [Fact]
    public void WithNative_RefusesAPayloadThePackInputDoesNotCarry()
    {
        Assert.Equal(2, ModulePackCommand.Run(
        [
            Path.Combine(root, "closure"),
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.9.2",
            "--framework-mvid", Identity,
            "--with-native", "runtimes/linux-x64/native/libnowhere.so",
            "--out", Path.Combine(root, "out-absent-native"),
        ]));
    }

    [Fact]
    public void AModuleWithNoNative_DeclaresNoNativeSectionAtAll()
    {
        // ── negative control 2. If the section were written empty rather than omitted, a consumer
        // could not tell "this producer ships none" from "this producer predates the section" —
        // and the assertion above would be reading a constant of every bundle.
        var outDir = Path.Combine(root, "out-no-native");
        Assert.Equal(0, ModulePackCommand.Run(
        [
            Path.Combine(root, "closure"),
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.8.0",
            "--framework-mvid", Identity,
            "--out", outDir,
        ]));

        var bundle = File.ReadAllBytes(
            Path.Combine(outDir, "MeshWeaver.Plugin.WidgetPkg.1.8.0.module.nupkg"));
        var (manifest, _) = BundleReader.ReadModule(bundle);

        Assert.Null(manifest!.Module!.NativeAssets);
        Assert.Empty(BundleReader.ReadModuleNativeAssets(bundle));
    }

    // ─────── #4367: what the derivation cannot carry REFUSES the pack — unless something NAMED carries it ───────

    /// <summary>
    /// Runs the command with its standard error captured, so a refusal can be asserted by what it
    /// SAYS and not only by its exit code. Safe to redirect: the runner is serialized
    /// (<c>test/xunit.runner.json</c>, <c>maxParallelThreads: 1</c>) and the original writer is
    /// restored in a <c>finally</c>, so no other test's output is taken or lost.
    /// </summary>
    private static (int Exit, string Error) PackCapturingErrors(IEnumerable<string> args)
    {
        var original = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            return (ModulePackCommand.Run([.. args]), error.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    private List<string> DepsClosurePackArgs(string version, string outDir) =>
    [
        Path.Combine(root, "closure"),
        "--deps-closure",
        "--module-name", "Widget",
        "--plugin", "WidgetPkg",
        "--package-version", version,
        "--framework-mvid", Identity,
        "--out", outDir,
    ];

    private static string BundleIn(string outDir, string version) =>
        Path.Combine(outDir, $"MeshWeaver.Plugin.WidgetPkg.{version}.module.nupkg");

    private const string RidSpecificManagedAsset = "runtimes/win-x64/lib/net10.0/RidPicky.dll";

    /// <summary>A module whose dependency ships a RID-agnostic managed copy AND a RID-specific one
    /// — the choice the flat closure (one slot per assembly name) cannot make at pack time. The
    /// publish folder holds the RID-agnostic copy at its root, exactly as a portable publish
    /// lays it out.</summary>
    private void RidSpecificManagedModule()
    {
        File.WriteAllBytes(Path.Combine(root, "closure", "RidPicky.dll"), "AGNOSTIC"u8.ToArray());
        File.WriteAllText(Path.Combine(root, "closure", "Widget.deps.json"), $$"""
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
              "targets": {
                ".NETCoreApp,Version=v10.0": {
                  "Widget/1.0.0": {
                    "dependencies": { "RidPicky": "1.0.0" },
                    "runtime": { "Widget.dll": {} }
                  },
                  "RidPicky/1.0.0": {
                    "runtime": { "lib/net10.0/RidPicky.dll": {} },
                    "runtimeTargets": {
                      "{{RidSpecificManagedAsset}}": { "rid": "win-x64", "assetType": "runtime" }
                    }
                  }
                }
              },
              "libraries": {
                "Widget/1.0.0": { "type": "project" },
                "RidPicky/1.0.0": { "type": "package" }
              }
            }
            """);
    }

    private const string UnprobedNative = "runtimes/linux-x64/nativeassets/net10.0/libodd.so";

    /// <summary>Where the loader probes for that same file and RID — the slot
    /// <c>--with-native</c> takes.</summary>
    private const string UnprobedNativesProbedSlot = "runtimes/linux-x64/native/libodd.so";

    /// <summary>A module whose dependency declares a native at a layout the loader never probes
    /// (<c>nativeassets/&lt;tfm&gt;/</c> — five segments, not the four the loader composes).</summary>
    private void UnprobedNativeModule()
    {
        File.WriteAllText(Path.Combine(root, "closure", "Widget.deps.json"), $$"""
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
              "targets": {
                ".NETCoreApp,Version=v10.0": {
                  "Widget/1.0.0": {
                    "dependencies": { "Odd.Natives": "1.0.0" },
                    "runtime": { "Widget.dll": {} }
                  },
                  "Odd.Natives/1.0.0": {
                    "runtimeTargets": {
                      "{{UnprobedNative}}": { "rid": "linux-x64", "assetType": "native" }
                    }
                  }
                }
              },
              "libraries": {
                "Widget/1.0.0": { "type": "project" },
                "Odd.Natives/1.0.0": { "type": "package" }
              }
            }
            """);
    }

    private void FileUnderClosure(string relative, string content)
    {
        var path = Path.Combine(root, "closure", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    /// 🚨 #4367, shape 1 — a RID-specific MANAGED asset nothing names REFUSES the pack, and the
    /// refusal names the package, the path and the exact value that lifts it.
    ///
    /// <para>🚨 The RID-agnostic <c>RidPicky.dll</c> IS in the closure here — it rides by
    /// derivation — and the pack is refused anyway. That is the rule, and this is its control: a
    /// copy that happens to ride is exactly the silent "which RID's copy" choice the refusal
    /// exists to stop, so only a copy the author NAMES counts. A rule keyed on "the file name is
    /// in the closure" would pass every such module unexamined, and this test would go green
    /// for the wrong reason.</para>
    /// </summary>
    [Fact]
    public void DepsClosure_ARidSpecificManagedAsset_NothingNamesIt_RefusesThePack()
    {
        RidSpecificManagedModule();
        var outDir = Path.Combine(root, "out-rid-refused");

        var (exit, error) = PackCapturingErrors(DepsClosurePackArgs("2.0.0", outDir));

        Assert.Equal(2, exit);
        Assert.False(Directory.Exists(outDir), "a refused pack must not have written anything");
        Assert.Contains("'RidPicky'", error, StringComparison.Ordinal);
        Assert.Contains(RidSpecificManagedAsset, error, StringComparison.Ordinal);
        Assert.Contains("--with RidPicky.dll", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// #4367, shape 1 — the SAME deps.json, with the remedy the refusal prescribes followed: the
    /// win-x64 copy flattened into the module folder root and named with <c>--with</c>. deps.json
    /// still declares the RID-specific asset; the pack must now succeed, or the refusal would be
    /// one its own advice cannot lift. And the bytes that ship are the copy the author put there.
    /// </summary>
    [Fact]
    public void DepsClosure_ARidSpecificManagedAsset_NamedWithWith_Packs()
    {
        RidSpecificManagedModule();
        File.WriteAllBytes(Path.Combine(root, "closure", "RidPicky.dll"), "WIN-X64"u8.ToArray());
        var outDir = Path.Combine(root, "out-rid-carried");

        var (exit, error) = PackCapturingErrors(
            [.. DepsClosurePackArgs("2.0.1", outDir), "--with", "RidPicky.dll"]);

        Assert.True(exit == 0, $"the prescribed remedy must lift the refusal; stderr: {error}");
        var (manifest, files) = BundleReader.ReadModule(File.ReadAllBytes(BundleIn(outDir, "2.0.1")));
        Assert.Contains("RidPicky.dll", manifest!.Module!.Assemblies!);
        Assert.Equal("WIN-X64",
            Encoding.UTF8.GetString(Assert.Single(files, f => f.FileName == "RidPicky.dll").Bytes));
    }

    /// <summary>
    /// 🚨 #4367, shape 2 — a native at a layout the loader never probes, with nothing carrying it,
    /// REFUSES the pack; the refusal names the package, the path, and BOTH carriers exactly — the
    /// probed slot for <c>--with-native</c> and the file name for <c>--with</c>.
    /// </summary>
    [Fact]
    public void DepsClosure_AnUnprobedNative_NothingCarriesIt_RefusesThePack()
    {
        UnprobedNativeModule();
        var outDir = Path.Combine(root, "out-native-refused");

        var (exit, error) = PackCapturingErrors(DepsClosurePackArgs("2.1.0", outDir));

        Assert.Equal(2, exit);
        Assert.False(Directory.Exists(outDir), "a refused pack must not have written anything");
        Assert.Contains("'Odd.Natives'", error, StringComparison.Ordinal);
        Assert.Contains(UnprobedNative, error, StringComparison.Ordinal);
        Assert.Contains($"--with-native {UnprobedNativesProbedSlot}", error, StringComparison.Ordinal);
        Assert.Contains("--with libodd.so", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// #4367, shape 2 — the SAME deps.json, carried the first way the refusal prescribes: laid out
    /// at the probed slot and named with <c>--with-native</c>. It packs, and the payload is in the
    /// native section at exactly that path.
    /// </summary>
    [Fact]
    public void DepsClosure_AnUnprobedNative_LaidOutAtTheProbedSlotWithWithNative_Packs()
    {
        UnprobedNativeModule();
        FileUnderClosure(UnprobedNativesProbedSlot, "ODD-ENGINE");
        var outDir = Path.Combine(root, "out-native-with-native");

        var (exit, error) = PackCapturingErrors(
            [.. DepsClosurePackArgs("2.1.1", outDir), "--with-native", UnprobedNativesProbedSlot]);

        Assert.True(exit == 0, $"the prescribed remedy must lift the refusal; stderr: {error}");
        var bundle = File.ReadAllBytes(BundleIn(outDir, "2.1.1"));
        var (manifest, _) = BundleReader.ReadModule(bundle);
        Assert.Equal([UnprobedNativesProbedSlot], manifest!.Module!.NativeAssets);
        Assert.Equal("ODD-ENGINE",
            Encoding.UTF8.GetString(Assert.Single(BundleReader.ReadModuleNativeAssets(bundle)).Bytes));
    }

    /// <summary>
    /// #4367, shape 2 — the SAME deps.json, carried the second way: flattened into the module
    /// folder root and named with <c>--with</c>. It packs, and the file rides the flat closure —
    /// which every landing writes into the module folder, the loader's last probe.
    /// </summary>
    [Fact]
    public void DepsClosure_AnUnprobedNative_FlattenedAndNamedWithWith_Packs()
    {
        UnprobedNativeModule();
        FileUnderClosure("libodd.so", "ODD-ENGINE");
        var outDir = Path.Combine(root, "out-native-with");

        var (exit, error) = PackCapturingErrors(
            [.. DepsClosurePackArgs("2.1.2", outDir), "--with", "libodd.so"]);

        Assert.True(exit == 0, $"the prescribed remedy must lift the refusal; stderr: {error}");
        var (manifest, files) = BundleReader.ReadModule(File.ReadAllBytes(BundleIn(outDir, "2.1.2")));
        Assert.Contains("libodd.so", manifest!.Module!.Assemblies!);
        Assert.Equal("ODD-ENGINE",
            Encoding.UTF8.GetString(Assert.Single(files, f => f.FileName == "libodd.so").Bytes));
    }

    /// <summary>
    /// 🚨 The anti-vacuity control on the carrier rule: something NAMED that is not the declared
    /// asset does not lift the refusal. The right file name in ANOTHER RID's slot is another
    /// RID's library, and <c>libOdd.so</c> is a different file from <c>libodd.so</c> on the
    /// case-sensitive filesystem a module runs on. A rule that accepted "any --with / --with-native
    /// at all" would pass both — and the remedy tests above would go green for the wrong reason.
    /// </summary>
    [Theory]
    [InlineData("--with-native", "runtimes/osx-arm64/native/libodd.so")]
    [InlineData("--with", "libOdd.so")]
    public void DepsClosure_AnUnprobedNative_IsNotCarriedBySomethingNamedThatIsNotIt(
        string flag, string value)
    {
        UnprobedNativeModule();
        FileUnderClosure(value, "NOT-THE-DECLARED-ENGINE");
        var outDir = Path.Combine(root, "out-native-wrong-carrier");

        var (exit, error) = PackCapturingErrors([.. DepsClosurePackArgs("2.1.3", outDir), flag, value]);

        Assert.Equal(2, exit);
        Assert.False(Directory.Exists(outDir), "a refused pack must not have written anything");
        Assert.Contains(UnprobedNative, error, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 #4367 — the SHARED LANE's remedy, which the coordinator's review found missing: the lane
    /// composes the pack arguments itself, so a CI-built module cannot pass <c>--with</c>. The
    /// refusal therefore prints csproj lines — a <c>Copy</c> after <c>Publish</c> from the key the
    /// portable publish lays the file out at, and the <c>MeshWeaverPackWith*</c> item
    /// <c>node-repo-module-pack.yml</c> turns into the flag. This reads those lines off the refusal
    /// VERBATIM (what an author pastes), does what they do — MSBuild's copy, then the lane's flag —
    /// and packs again: the lines must lift the refusal, or the advice is a dead end in the only
    /// lane that runs it. (The lane's read of the item is executed by
    /// <c>.github/scripts/test-module-pack-with.py</c>; the whole chain was run for real against the
    /// SDK once, see Doc/Architecture/Modules.)
    /// </summary>
    [Theory]
    [InlineData("rid-specific managed")]
    [InlineData("unprobed native")]
    public void DepsClosure_TheSharedLaneCsprojLinesInTheRefusal_WhenApplied_LiftIt(string shape)
    {
        var managed = shape == "rid-specific managed";
        var declared = managed ? RidSpecificManagedAsset : UnprobedNative;
        if (managed)
            RidSpecificManagedModule();
        else
            UnprobedNativeModule();
        // A PORTABLE publish lays every runtimeTargets asset out at its declared key.
        FileUnderClosure(declared, managed ? "WIN-X64" : "ODD-ENGINE");

        var (refusedExit, refusal) = PackCapturingErrors(
            DepsClosurePackArgs("2.2.0", Path.Combine(root, "out-lane-refused")));
        Assert.Equal(2, refusedExit);

        var copy = Regex.Match(refusal,
            """<Target Name="[A-Za-z0-9_]+" AfterTargets="Publish"><Copy SourceFiles="\$\(PublishDir\)(?<src>[^"]+)" DestinationFolder="\$\(PublishDir\)(?<dst>[^"]*)" /></Target>""");
        var item = Regex.Match(refusal,
            """<ItemGroup><(?<type>MeshWeaverPackWith(Native)?) Include="(?<value>[^"]+)" /></ItemGroup>""");
        Assert.True(copy.Success && item.Success,
            $"the refusal must print the csproj lines the shared lane needs; stderr: {refusal}");
        Assert.Equal(declared, copy.Groups["src"].Value);

        // What those lines DO: MSBuild's Copy after Publish…
        var publish = Path.Combine(root, "closure");
        var source = Path.Combine(publish, copy.Groups["src"].Value);
        var destination = Path.Combine(publish, copy.Groups["dst"].Value);
        Directory.CreateDirectory(destination);
        File.Copy(source, Path.Combine(destination, Path.GetFileName(source)), overwrite: true);
        // …then the lane's read: one flag per item.
        var flag = item.Groups["type"].Value == "MeshWeaverPackWithNative" ? "--with-native" : "--with";
        var outDir = Path.Combine(root, "out-lane-carried");

        var (exit, error) = PackCapturingErrors(
            [.. DepsClosurePackArgs("2.2.1", outDir), flag, item.Groups["value"].Value]);

        Assert.True(exit == 0, $"the csproj lines the refusal prints must lift it; stderr: {error}");
        var bundle = File.ReadAllBytes(BundleIn(outDir, "2.2.1"));
        if (managed)
        {
            var (_, files) = BundleReader.ReadModule(bundle);
            Assert.Equal("WIN-X64",
                Encoding.UTF8.GetString(Assert.Single(files, f => f.FileName == "RidPicky.dll").Bytes));
        }
        else
        {
            var native = Assert.Single(BundleReader.ReadModuleNativeAssets(bundle));
            Assert.Equal(UnprobedNativesProbedSlot, native.RelativePath);
            Assert.Equal("ODD-ENGINE", Encoding.UTF8.GetString(native.Bytes));
        }
    }

    [Fact]
    public void DepsClosure_SkipsFrameworkTrimmedFiles_WhenOthersArePresent()
    {
        // Gadget.Sdk was copied to the output; Microsoft.Extensions.Options was FRAMEWORK-TRIMMED
        // by the SDK (resolved to the shared framework, so CopyLocalLockFileAssemblies does not
        // copy it). The bundle carries what is present and skips what the consumer's runtime
        // provides — loudly, never as an error, because failing here blocked six of fourteen
        // modules on CI while the same pack passed on a dev machine whose SDK had copied the file.
        File.WriteAllBytes(Path.Combine(root, "closure", "Gadget.Sdk.dll"), "SDK"u8.ToArray());
        File.WriteAllText(Path.Combine(root, "closure", "Widget.deps.json"), """
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
              "targets": {
                ".NETCoreApp,Version=v10.0": {
                  "Widget/1.0.0": {
                    "dependencies": { "Gadget.Sdk": "2.0.0", "Microsoft.Extensions.Options": "10.0.0" },
                    "runtime": { "Widget.dll": {} }
                  },
                  "Gadget.Sdk/2.0.0": { "runtime": { "lib/net10.0/Gadget.Sdk.dll": {} } },
                  "Microsoft.Extensions.Options/10.0.0": { "runtime": { "lib/net10.0/Microsoft.Extensions.Options.dll": {} } }
                }
              },
              "libraries": {
                "Widget/1.0.0": { "type": "project" },
                "Gadget.Sdk/2.0.0": { "type": "package" },
                "Microsoft.Extensions.Options/10.0.0": { "type": "package" }
              }
            }
            """);

        var outDir = Path.Combine(root, "out-trimmed");
        var exit = ModulePackCommand.Run(
        [
            Path.Combine(root, "closure"),
            "--deps-closure",
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.5.0",
            "--framework-mvid", Identity,
            "--out", outDir,
        ]);

        Assert.Equal(0, exit);
        var (manifest, files) = BundleReader.ReadModule(File.ReadAllBytes(
            Path.Combine(outDir, "MeshWeaver.Plugin.WidgetPkg.1.5.0.module.nupkg")));
        Assert.Contains("Gadget.Sdk.dll", manifest!.Module!.Assemblies!);
        Assert.DoesNotContain("Microsoft.Extensions.Options.dll", manifest.Module.Assemblies!);
        Assert.Contains(files, f => f.FileName == "Gadget.Sdk.dll");
    }

    [Fact]
    public void DepsClosure_AllOwnDepsFrameworkTrimmed_PacksEntryOnly_WhenTheFolderHasPackageAssets()
    {
        // The Notifications shape in a publish folder: the module's OWN dependencies are all
        // framework-trimmed (absent), but the folder plainly materializes package assets — a
        // platform-reachable package file is right there. That is a valid entry-only bundle,
        // not a broken lane; refusing it blocked 2 of 14 modules on CI (2026-08-20).
        File.WriteAllBytes(Path.Combine(root, "closure", "Autofac.dll"), "AF"u8.ToArray());
        File.WriteAllText(Path.Combine(root, "closure", "Widget.deps.json"), """
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
              "targets": {
                ".NETCoreApp,Version=v10.0": {
                  "Widget/1.0.0": {
                    "dependencies": { "MeshWeaver.AI": "3.0.0", "Microsoft.Extensions.Options": "10.0.0" },
                    "runtime": { "Widget.dll": {} }
                  },
                  "MeshWeaver.AI/3.0.0": {
                    "dependencies": { "Autofac": "8.0.0" },
                    "runtime": { "MeshWeaver.AI.dll": {} }
                  },
                  "Autofac/8.0.0": { "runtime": { "lib/net8.0/Autofac.dll": {} } },
                  "Microsoft.Extensions.Options/10.0.0": { "runtime": { "lib/net10.0/Microsoft.Extensions.Options.dll": {} } }
                }
              },
              "libraries": {
                "Widget/1.0.0": { "type": "project" },
                "MeshWeaver.AI/3.0.0": { "type": "project" },
                "Autofac/8.0.0": { "type": "package" },
                "Microsoft.Extensions.Options/10.0.0": { "type": "package" }
              }
            }
            """);

        var outDir = Path.Combine(root, "out-fw-only");
        var exit = ModulePackCommand.Run(
        [
            Path.Combine(root, "closure"),
            "--deps-closure",
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.6.0",
            "--framework-mvid", Identity,
            "--out", outDir,
        ]);

        Assert.Equal(0, exit);
        var (manifest, _) = BundleReader.ReadModule(File.ReadAllBytes(
            Path.Combine(outDir, "MeshWeaver.Plugin.WidgetPkg.1.6.0.module.nupkg")));
        // Entry only: the trimmed dependency stays out, and the platform-reachable Autofac —
        // present in the folder — must NOT ride either.
        Assert.DoesNotContain("Microsoft.Extensions.Options.dll", manifest!.Module!.Assemblies!);
        Assert.DoesNotContain("Autofac.dll", manifest.Module.Assemblies!);
    }

    [Fact]
    public void DepsClosure_WithTheFileMissingFromTheOutput_IsARefusal()
    {
        // deps.json names a dependency that is NOT in the folder — the build ran without
        // CopyLocalLockFileAssemblies, so NOTHING was copied. Packing anyway would land a module
        // that faults at first use, which is the outage this flag exists to close. (A PARTIAL
        // absence is the framework-trim case above — skipped, not refused.)
        File.WriteAllText(Path.Combine(root, "closure", "Widget.deps.json"), """
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
              "targets": {
                ".NETCoreApp,Version=v10.0": {
                  "Widget/1.0.0": {
                    "dependencies": { "Absent.Sdk": "1.0.0" },
                    "runtime": { "Widget.dll": {} }
                  },
                  "Absent.Sdk/1.0.0": { "runtime": { "lib/net10.0/Absent.Sdk.dll": {} } }
                }
              },
              "libraries": {
                "Widget/1.0.0": { "type": "project" },
                "Absent.Sdk/1.0.0": { "type": "package" }
              }
            }
            """);

        var exit = ModulePackCommand.Run(
        [
            Path.Combine(root, "closure"),
            "--deps-closure",
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.4.0",
            "--framework-mvid", Identity,
            "--out", Path.Combine(root, "out-missing"),
        ]);

        Assert.Equal(2, exit);
        Assert.False(Directory.Exists(Path.Combine(root, "out-missing")),
            "a refused invocation must not have written anything");
    }

    // ─────── #3732: the platform-shipped witness, measured off the host, applied to the RIDES ───────

    /// <summary>Bytes of the copy the PLATFORM ships — deliberately different from the copy the
    /// module output holds, because that difference IS the condition under test.</summary>
    private const string PlatformBuild = "BUILD-A (the copy the platform ships)";

    /// <summary>Bytes of the copy the module output holds — the ride.</summary>
    private const string ModuleBuild = "BUILD-B (the copy that would ride the bundle)";

    /// <summary>
    /// A platform host directory shaped like a portal image's <c>/app</c>: an app closure carrying
    /// <c>MeshWeaver.Blazor.Views</c>, a surface manifest, and a SEEDED module under
    /// <c>modules/&lt;Name&gt;/</c> — the three ways an image ships an assembly.
    /// </summary>
    private string PlatformApp()
    {
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "MeshWeaver.Blazor.Views.dll"), PlatformBuild);
        File.WriteAllText(Path.Combine(app, "MeshWeaver.Graph.dll"), PlatformBuild);
        File.WriteAllLines(Path.Combine(app, "meshweaver-surface.manifest"),
        [
            "MeshWeaver.Blazor.Views=0000000000000000000000000000000000000000000000000000000000000000",
            "MeshWeaver.Graph=1111111111111111111111111111111111111111111111111111111111111111",
        ]);
        var seeded = Path.Combine(app, "modules", "MeshWeaver.Markdown.Collaboration");
        Directory.CreateDirectory(seeded);
        File.WriteAllText(
            Path.Combine(seeded, "MeshWeaver.Markdown.Collaboration.dll"), PlatformBuild);
        return app;
    }

    /// <summary>The module output as the lane hands it to the packer: the entry, two MeshWeaver.*
    /// siblings the host also has (at a DIFFERENT build), and one it does not.</summary>
    private void ModuleOutputWithRides()
    {
        var closure = Path.Combine(root, "closure");
        File.WriteAllText(
            Path.Combine(closure, "MeshWeaver.Markdown.Collaboration.dll"), ModuleBuild);
        File.WriteAllText(Path.Combine(closure, "MeshWeaver.Markdown.Collaboration.pdb"), ModuleBuild);
        File.WriteAllText(Path.Combine(closure, "MeshWeaver.Blazor.Views.dll"), ModuleBuild);
        // The name the platform genuinely does NOT ship — the anti-vacuity control.
        File.WriteAllText(Path.Combine(closure, "MeshWeaver.Maps.dll"), ModuleBuild);
    }

    private int PackWidget(string outDir, string? platformApp)
    {
        var args = new List<string>
        {
            Path.Combine(root, "closure"),
            "--module-name", "Widget",
            "--plugin", "WidgetPkg",
            "--package-version", "1.9.0",
            "--framework-mvid", Identity,
            "--with", "MeshWeaver.Markdown.Collaboration.dll",
            "--with", "MeshWeaver.Markdown.Collaboration.pdb",
            "--with", "MeshWeaver.Blazor.Views.dll",
            "--with", "MeshWeaver.Maps.dll",
            "--out", outDir,
        };
        if (platformApp is not null)
        {
            args.Add("--platform-app");
            args.Add(platformApp);
        }
        return ModulePackCommand.Run([.. args]);
    }

    /// <summary>
    /// 🚨 <b>THE CONDITION, REPRODUCED (#3732).</b> Without the witness the bundle carries a SECOND
    /// BUILD of two assemblies the platform host already has — one from its app closure
    /// (<c>MeshWeaver.Blazor.Views</c>, which no package declares as a module at all: it arrives
    /// only because a module-owned sibling references it) and one from its seeded
    /// <c>modules/&lt;Name&gt;/</c> lane (<c>MeshWeaver.Markdown.Collaboration</c>).
    ///
    /// <para>Both bind by a strictly synchronised <c>AssemblyVersion</c>, so the two copies are ONE
    /// identity: the loader keeps whichever it saw first and the loser is never in memory. On
    /// memex.systemorph.com that loser was <c>MeshWeaver.Blazor.Views</c> — both pods reported
    /// <c>pending_module_activation</c> Degraded ("landed but not yet loaded"), and every skinned
    /// <c>StackControl</c> rendered through <c>FallbackHtml</c>.</para>
    ///
    /// <para>This is the state of the lane BEFORE the fix, asserted rather than described — without
    /// it the test below could pass because the packer drops everything, or because the fixture
    /// never carried the rides at all.</para>
    /// </summary>
    [Fact]
    public void WithoutTheWitness_TheBundleCarriesASecondBuildOfWhatThePlatformShips()
    {
        var app = PlatformApp();
        ModuleOutputWithRides();

        var outDir = Path.Combine(root, "out-unwitnessed");
        Assert.Equal(0, PackWidget(outDir, platformApp: null));

        var (manifest, files) = BundleReader.ReadModule(File.ReadAllBytes(
            Path.Combine(outDir, "MeshWeaver.Plugin.WidgetPkg.1.9.0.module.nupkg")));

        Assert.Contains("MeshWeaver.Blazor.Views.dll", manifest!.Module!.Assemblies!);
        Assert.Contains("MeshWeaver.Markdown.Collaboration.dll", manifest.Module.Assemblies!);

        // …and they are a DIFFERENT BUILD from the host's own copies, which is what makes one
        // assembly name reach a process at two builds rather than merely costing bytes.
        var ridden = files.Single(f => f.FileName == "MeshWeaver.Blazor.Views.dll");
        Assert.Equal(ModuleBuild, Encoding.UTF8.GetString(ridden.Bytes));
        Assert.Equal(PlatformBuild,
            File.ReadAllText(Path.Combine(app, "MeshWeaver.Blazor.Views.dll")));
        Assert.NotEqual(
            File.ReadAllText(Path.Combine(app, "modules", "MeshWeaver.Markdown.Collaboration",
                "MeshWeaver.Markdown.Collaboration.dll")),
            Encoding.UTF8.GetString(
                files.Single(f => f.FileName == "MeshWeaver.Markdown.Collaboration.dll").Bytes));
    }

    /// <summary>
    /// 🚨 <b>THE FIX (#3732).</b> With the host in hand, every <c>MeshWeaver.*</c> file the closure
    /// would carry is measured against what that host ACTUALLY ships and dropped when it already
    /// has it — from the manifest AND from the archive, symbols included — whichever of the three
    /// witnesses answered.
    ///
    /// <para><b>And the anti-vacuity control is in the same assertion:</b>
    /// <c>MeshWeaver.Maps</c> is a <c>MeshWeaver.*</c> sibling the host does NOT ship, so it must
    /// STILL ride. A packer that simply dropped every platform-named file would pass the first half
    /// and fail here — and would reintroduce the opposite defect, a name that reaches a mesh from
    /// nowhere at all (#3335's <c>MeshWeaver.Maps</c> line, exactly).</para>
    /// </summary>
    [Fact]
    public void WithTheWitness_ThePlatformsOwnCopiesAreDropped_AndOnlyThose()
    {
        var app = PlatformApp();
        ModuleOutputWithRides();

        var outDir = Path.Combine(root, "out-witnessed");
        Assert.Equal(0, PackWidget(outDir, app));

        var (manifest, files) = BundleReader.ReadModule(File.ReadAllBytes(
            Path.Combine(outDir, "MeshWeaver.Plugin.WidgetPkg.1.9.0.module.nupkg")));

        // Dropped: the app-closure copy, the seeded-module copy, and the symbols that came with it.
        Assert.DoesNotContain("MeshWeaver.Blazor.Views.dll", manifest!.Module!.Assemblies!);
        Assert.DoesNotContain("MeshWeaver.Markdown.Collaboration.dll", manifest.Module.Assemblies!);
        Assert.DoesNotContain("MeshWeaver.Markdown.Collaboration.pdb", manifest.Module.Assemblies!);
        Assert.DoesNotContain(files, f => f.FileName.StartsWith(
            "MeshWeaver.Blazor.Views", StringComparison.Ordinal));
        Assert.DoesNotContain(files, f => f.FileName.StartsWith(
            "MeshWeaver.Markdown.Collaboration", StringComparison.Ordinal));

        // Kept: the entry, and the sibling the platform does not ship.
        Assert.Contains("Widget.dll", manifest.Module.Assemblies!);
        Assert.Contains("MeshWeaver.Maps.dll", manifest.Module.Assemblies!);
        Assert.Contains(files, f => f.FileName == "MeshWeaver.Maps.dll");
        Assert.Contains(files, f => f.FileName == "Widget.dll");
    }

    /// <summary>
    /// 🚨 The ENTRY is never judged by this step, and that boundary is deliberate. A module the
    /// image seeds under <c>modules/&lt;Name&gt;/</c> is MEANT to be superseded by a landed bundle
    /// of itself (a usable persisted entry overrides the same-named baseline in place), so dropping
    /// the entry would delete the module from its own bundle. The host-vs-entry two-producer case
    /// is <c>BakeHost.ShippedByHostProblem</c>'s, at the bake, where both provenances are in one
    /// hand.
    /// </summary>
    [Fact]
    public void TheEntryAssemblyIsNeverDropped_EvenWhenTheImageSeedsThatVeryModule()
    {
        var app = PlatformApp();
        var closure = Path.Combine(root, "closure2");
        Directory.CreateDirectory(closure);
        File.WriteAllText(
            Path.Combine(closure, "MeshWeaver.Markdown.Collaboration.dll"), ModuleBuild);

        var outDir = Path.Combine(root, "out-entry");
        var exit = ModulePackCommand.Run(
        [
            closure,
            "--module-name", "MeshWeaver.Markdown.Collaboration",
            "--plugin", "Essentials",
            "--package-version", "2.0.0",
            "--framework-mvid", Identity,
            "--platform-app", app,
            "--out", outDir,
        ]);

        Assert.Equal(0, exit);
        var (manifest, files) = BundleReader.ReadModule(File.ReadAllBytes(
            Path.Combine(outDir, "MeshWeaver.Plugin.Essentials.2.0.0.module.nupkg")));
        Assert.Contains("MeshWeaver.Markdown.Collaboration.dll", manifest!.Module!.Assemblies!);
        Assert.Contains(files, f => f.FileName == "MeshWeaver.Markdown.Collaboration.dll");
    }

    /// <summary>
    /// 🚨 A witness that reads nothing must REFUSE, never answer "the platform ships nothing" — that
    /// answer strips nothing while logging exactly like a clean measurement, which is the
    /// gate-that-cannot-fail shape CI forbids.
    /// </summary>
    [Fact]
    public void APlatformAppThatIsNotOne_IsRefused_AndWritesNothing()
    {
        ModuleOutputWithRides();
        var notAnApp = Path.Combine(root, "not-an-app");
        Directory.CreateDirectory(notAnApp);
        File.WriteAllText(Path.Combine(notAnApp, "readme.txt"), "no assemblies, no manifest");

        var outDir = Path.Combine(root, "out-bad-witness");
        var exit = PackWidget(outDir, notAnApp);

        Assert.Equal(2, exit);
        Assert.False(Directory.Exists(outDir),
            "a refused invocation must not have written a bundle");
    }
}
