#pragma warning disable CS1591

using System.Text;
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
}
