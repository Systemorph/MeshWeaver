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
            "--out", Path.Combine(root, "out"),
        };
        args[args.IndexOf(option) + 1] = value;

        var exit = ModulePackCommand.Run([.. args]);

        Assert.Equal(2, exit);
        Assert.False(Directory.Exists(Path.Combine(root, "out")),
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
        // No MeshWeaver.Graph.dll in the closure folder: the diagnostic MVID is simply
        // unrecorded — warned, never an error, never a gate.
        Assert.Null(manifest.FrameworkMvid);
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
            "--out", Path.Combine(root, "out-missing"),
        ]);

        Assert.Equal(2, exit);
        Assert.False(Directory.Exists(Path.Combine(root, "out-missing")),
            "a refused invocation must not have written anything");
    }
}
