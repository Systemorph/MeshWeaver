using System.Collections.Immutable;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The CONTAINER lane's half of #4126: a module's LOADABLE NATIVE payloads are resolved against the
/// image, ride the bundle, and land at the exact path the module loader probes.
///
/// <para><b>What was measured, and what it cost.</b> <c>ContainerReferenceSet</c> read only the
/// <c>runtime</c> section of the image's deps.json, and <c>Resolve</c> matched a package by the
/// ASSEMBLY it contributes (falling back to <c>&lt;id&gt;.dll</c>). A package whose ONLY
/// contribution is native — <c>SQLitePCLRaw.lib.e_sqlite3</c>, the engine
/// <c>MeshWeaver.AppleMessages</c> needs and the GHSA-2m69-gcr7-jv3q pin every SDK build carries —
/// therefore answered <c>Supplied = false</c> against an image that ships <c>libe_sqlite3.so</c> in
/// <c>/app</c>, and <c>ProjectBuild</c> reds an unresolved <c>PackageReference</c>. So a
/// <c>build: container</c> module could not even DECLARE its engine; and had it got past that, the
/// closure walk filed the package under <c>Missing</c> and nothing laid a <c>runtimes/</c> tree
/// beside the module for it to ride in.</para>
///
/// <para>🚨 <b>The deps.json shapes here are the two REAL ones, measured 2026-09-15 on an actual
/// <c>SQLitePCLRaw.lib.e_sqlite3</c> publish rather than written from memory.</b> A PORTABLE
/// publish leaves 29 <c>runtimeTargets</c> entries with <c>assetType: "native"</c> and a
/// <c>runtimes/</c> tree on disk; <c>-r linux-x64</c> — which is what every MeshWeaver image is —
/// resolves them into a one-entry <c>native</c> section whose KEY is still
/// <c>runtimes/linux-x64/native/libe_sqlite3.so</c> while the FILE sits FLAT beside the app. A
/// reader that knows only one of the two answers "this image has no natives" about an image that
/// has them, which is why both are pinned below.</para>
/// </summary>
public class NativeClosureTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mw-native-closure-{Guid.NewGuid():N}");

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static void Dll(string directory, string name) =>
        File.WriteAllBytes(Path.Combine(directory, name + ".dll"), [0x4D, 0x5A]);

    private const string LinuxNative = "runtimes/linux-x64/native/libe_sqlite3.so";
    private const string OsxNative = "runtimes/osx-arm64/native/libe_sqlite3.dylib";

    /// <summary>
    /// The RID-SPECIFIC image: <c>SQLitePCLRaw.lib.e_sqlite3</c> contributes a <c>native</c>
    /// section and NO <c>runtime</c> section, and its file is flattened into <c>/app</c>.
    /// </summary>
    private ContainerReferenceSet RidSpecificImage(bool withFile = true)
    {
        var app = Path.Combine(_root, "app");
        Directory.CreateDirectory(app);
        Dll(app, "MeshWeaver.Data");
        Dll(app, "SQLitePCLRaw.core");
        if (withFile)
            File.WriteAllText(Path.Combine(app, "libe_sqlite3.so"), "ELF-linux-x64");
        File.WriteAllText(Path.Combine(app, "Portal.deps.json"), """
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0/linux-x64" },
              "targets": {
                ".NETCoreApp,Version=v10.0/linux-x64": {
                  "Portal/1.0.0": { "dependencies": { "SQLitePCLRaw.bundle_e_sqlite3": "2.1.11" } },
                  "SQLitePCLRaw.bundle_e_sqlite3/2.1.11": {
                    "dependencies": { "SQLitePCLRaw.lib.e_sqlite3": "3.50.3", "SQLitePCLRaw.core": "2.1.11" }
                  },
                  "SQLitePCLRaw.core/2.1.11": {
                    "runtime": { "lib/netstandard2.0/SQLitePCLRaw.core.dll": { "assemblyVersion": "2.1.11.0" } }
                  },
                  "SQLitePCLRaw.lib.e_sqlite3/3.50.3": {
                    "native": { "runtimes/linux-x64/native/libe_sqlite3.so": { "fileVersion": "0.0.0.0" } }
                  },
                  "MeshWeaver.Data/3.0.0": {
                    "runtime": { "MeshWeaver.Data.dll": { "assemblyVersion": "3.0.0.0" } }
                  }
                }
              },
              "libraries": {
                "Portal/1.0.0": { "type": "project" },
                "SQLitePCLRaw.bundle_e_sqlite3/2.1.11": { "type": "package" },
                "SQLitePCLRaw.core/2.1.11": { "type": "package" },
                "SQLitePCLRaw.lib.e_sqlite3/3.50.3": { "type": "package" },
                "MeshWeaver.Data/3.0.0": { "type": "package" }
              }
            }
            """);
        return ContainerReferenceSet.Read(app, trustedPlatformAssemblies: string.Empty);
    }

    /// <summary>
    /// The PORTABLE image: the same package declares every RID under <c>runtimeTargets</c>, the
    /// tree is preserved on disk, and the section also carries the shapes that must NOT be
    /// carried — a static library, a RID-specific MANAGED asset, and a native at a layout the
    /// loader never probes.
    /// </summary>
    private ContainerReferenceSet PortableImage()
    {
        var app = Path.Combine(_root, "portable");
        Directory.CreateDirectory(app);
        Dll(app, "MeshWeaver.Data");
        foreach (var relative in new[]
                 {
                     LinuxNative, OsxNative,
                     "runtimes/ios-arm64/native/e_sqlite3.a",
                     "runtimes/browser-wasm/nativeassets/net9.0/e_sqlite3.so",
                     "runtimes/win-x64/lib/net10.0/Rid.Specific.Managed.dll",
                 })
        {
            var path = Path.Combine(app, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "payload:" + relative);
        }
        File.WriteAllText(Path.Combine(app, "Portal.deps.json"), """
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
              "targets": {
                ".NETCoreApp,Version=v10.0": {
                  "Portal/1.0.0": { "dependencies": { "SQLitePCLRaw.lib.e_sqlite3": "3.50.3" } },
                  "SQLitePCLRaw.lib.e_sqlite3/3.50.3": {
                    "runtimeTargets": {
                      "runtimes/linux-x64/native/libe_sqlite3.so": { "rid": "linux-x64", "assetType": "native" },
                      "runtimes/osx-arm64/native/libe_sqlite3.dylib": { "rid": "osx-arm64", "assetType": "native" },
                      "runtimes/ios-arm64/native/e_sqlite3.a": { "rid": "ios-arm64", "assetType": "native" },
                      "runtimes/browser-wasm/nativeassets/net9.0/e_sqlite3.so": { "rid": "browser-wasm", "assetType": "native" },
                      "runtimes/win-x64/lib/net10.0/Rid.Specific.Managed.dll": { "rid": "win-x64", "assetType": "runtime" }
                    }
                  },
                  "MeshWeaver.Data/3.0.0": {
                    "runtime": { "MeshWeaver.Data.dll": { "assemblyVersion": "3.0.0.0" } }
                  }
                }
              },
              "libraries": {
                "Portal/1.0.0": { "type": "project" },
                "SQLitePCLRaw.lib.e_sqlite3/3.50.3": { "type": "package" },
                "MeshWeaver.Data/3.0.0": { "type": "package" }
              }
            }
            """);
        return ContainerReferenceSet.Read(app, trustedPlatformAssemblies: string.Empty);
    }

    [Fact]
    public void ANativeOnlyPackageIsSUPPLIED_soAContainerModuleCanDeclareItsEngine()
    {
        var resolution = RidSpecificImage().Resolve("SQLitePCLRaw.lib.e_sqlite3");

        resolution.Supplied.Should().BeTrue(
            "the image's own deps.json says this package contributes runtimes/linux-x64/native/"
            + "libe_sqlite3.so and the file is in /app — answering 'the container does not supply "
            + "it' refused a CVE-patched engine the container demonstrably ships, which is what "
            + "kept MeshWeaver.AppleMessages on a conditional pin");
        resolution.AssemblyPaths.Should().BeEmpty(
            "it contributes no managed assembly at all — that is the whole reason the "
            + "assembly-matching rule answered no");
        resolution.Natives.Should().ContainSingle()
            .Which.RelativePath.Should().Be(LinuxNative);
    }

    [Fact]
    public void ThePackageIsNOTSuppliedWhenTheIMAGEDoesNotActuallyCarryTheFile()
    {
        // 🚨 The anti-vacuity control for the case above: if `Supplied` answered off the
        // DECLARATION alone, it would be true here too, and the verdict would say nothing about
        // the image at all. The declaration is identical; only the bytes are gone.
        var resolution = RidSpecificImage(withFile: false).Resolve("SQLitePCLRaw.lib.e_sqlite3");

        resolution.Supplied.Should().BeFalse(
            "a package whose payload is declared and absent is exactly the bundle that faults at "
            + "first use — the refusal by name is the right answer there");
        resolution.Natives.Should().BeEmpty();
    }

    [Fact]
    public void TheNativeRIDESTheBundle_atThePathTheLoaderProbes()
    {
        var closure = PrivateClosure.Derive(
            ["SQLitePCLRaw.bundle_e_sqlite3"], RidSpecificImage(), shelf: null);

        var native = closure.Natives.Should().ContainSingle().Subject;
        native.RelativePath.Should().Be(LinuxNative,
            "ModuleNativeAssets composes its probe from exactly runtimes/<rid>/native/<file> and "
            + "has no recursive walk — a payload carried at any other path reads as shipped and "
            + "behaves as absent");
        native.PackageId.Should().Be("SQLitePCLRaw.lib.e_sqlite3",
            "the walk reaches it through the bundle package's dependency edge, exactly as the "
            + "managed rides are reached");
        native.Source.Should().Be("the image");
        File.ReadAllText(native.SourcePath).Should().Be("ELF-linux-x64",
            "the bytes come from the FLAT file a RID-specific publish left in /app, while the "
            + "path they must land at is the one the deps.json declares");
        closure.NativesMissing.Should().BeEmpty();
    }

    [Fact]
    public void APORTABLEImageCarriesEveryRidAndDropsWhatNothingCanLoad()
    {
        var closure = PrivateClosure.Derive(
            ["SQLitePCLRaw.lib.e_sqlite3"], PortableImage(), shelf: null);

        closure.Natives.Select(n => n.RelativePath).Should().Equal([LinuxNative, OsxNative],
            "a portable publish keeps every RID's payload at its own path, and the host picks its "
            + "own at load time");
        closure.Natives.Select(n => n.RelativePath).Should()
            .NotContain(p => p.EndsWith(".a", StringComparison.Ordinal),
                "a static library is a link-time input nothing ever loads — the same exclusion the "
                + "in-image lane has applied since #1728")
            .And.NotContain(p => p.Contains("nativeassets", StringComparison.Ordinal),
                "runtimes/<rid>/nativeassets/<tfm>/<file> is five segments, not the four the "
                + "loader composes — carrying it would be bytes nothing ever probes")
            .And.NotContain(p => p.Contains("Rid.Specific.Managed", StringComparison.Ordinal),
                "assetType 'runtime' is a RID-specific MANAGED assembly and belongs to the flat "
                + "closure's rules, which have no way to choose a RID");
    }

    [Fact]
    public void ADeclaredNativeWithNoBytesAnywhereIsREPORTED_neverSilentlyDropped()
    {
        var closure = PrivateClosure.Derive(
            ["SQLitePCLRaw.bundle_e_sqlite3"], RidSpecificImage(withFile: false), shelf: null);

        closure.Natives.Should().BeEmpty();
        closure.NativesMissing.Should().Equal([LinuxNative],
            "it is the one drop that produces no compile error, no load error and no rendering "
            + "defect — only a DllNotFoundException at the first P/Invoke, arbitrarily far from "
            + "the build that caused it");
    }

    [Fact]
    public void ThePlatformsOwnNativesNeverRide()
    {
        // MeshWeaver.* is a ProjectReference, never a ride — the same rule the managed half
        // applies, and for the same #143 reason.
        var closure = PrivateClosure.Derive(["MeshWeaver.Data"], PortableImage(), shelf: null);

        closure.Natives.Should().BeEmpty();
    }

    [Fact]
    public void TheEmittedLayoutIsTheProbedOne_andTheManifestIsItsProvenance()
    {
        var source = Path.Combine(_root, "src-native");
        Directory.CreateDirectory(source);
        var file = Path.Combine(source, "libe_sqlite3.so");
        File.WriteAllText(file, "ELF-linux-x64");
        var output = Path.Combine(_root, "packdir");
        Directory.CreateDirectory(output);
        var warnings = new List<string>();

        var laid = ProjectBuild.EmitNativeClosure(
            output, "MeshWeaver.Test.Sqlite",
            [
                new PrivateClosure.NativeRide(LinuxNative, file, "SQLitePCLRaw.lib.e_sqlite3", "the image"),
                // A path the loader never probes must not be laid out even if a caller hands one
                // over: the predicate is the boundary, not a formality.
                new PrivateClosure.NativeRide(
                    "runtimes/linux-x64/other/native/libx.so", file, "Bogus", "the image"),
            ],
            _ => { }, warnings.Add);

        laid.Should().Equal([LinuxNative]);
        File.Exists(Path.Combine(output, "runtimes", "linux-x64", "native", "libe_sqlite3.so"))
            .Should().BeTrue("the pack input must hold the bytes at the module-relative path the "
                + "bundle then carries them at");
        File.ReadAllLines(Path.Combine(output, ProjectBuild.NativeManifestName))
            .Should().Equal([LinuxNative],
                "the manifest is the PROVENANCE the pack lane declares --with-native from, so a "
                + "payload the builder did not derive cannot reach a bundle");
        warnings.Should().ContainSingle()
            .Which.Should().Contain("runtimes/linux-x64/other/native/libx.so");
    }

    [Fact]
    public void NoNativesMeansNoManifest_soItsAbsenceIsAStatement()
    {
        var output = Path.Combine(_root, "empty-packdir");
        Directory.CreateDirectory(output);

        ProjectBuild.EmitNativeClosure(
            output, "MeshWeaver.Test.NoEngine", ImmutableArray<PrivateClosure.NativeRide>.Empty,
            _ => { }, _ => { });

        File.Exists(Path.Combine(output, ProjectBuild.NativeManifestName)).Should().BeFalse(
            "the pack lane reads the file's presence as 'this module ships natives'; writing an "
            + "empty one would make 'none' and 'the builder never looked' the same state");
    }
}
