using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The reference set's contract — the C# port of <c>MeshWeaver.Plugins/scripts/container-refs.py</c>
/// read from <c>/app</c> instead of an extracted image.
///
/// <para>Two invariants, and both are about being WRONG rather than being incomplete: every read
/// FAILS CLOSED (a partial set produces a build that is green against a reference set nobody can
/// name), and a package is matched by the ASSEMBLY FILE ON DISK, never by its id alone (a package
/// can ship an assembly under a different name, or none at all).</para>
/// </summary>
public class ContainerReferenceSetTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mw-container-refs-{Guid.NewGuid():N}");

    private string App(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>A minimal but structurally real host deps.json.</summary>
    private static string Deps(
        string packages = """
            "Some.Package/4.2.0": { "type": "package", "serviceable": true },
            "Renamed.Package/1.0.0": { "type": "package", "serviceable": true },
            "Metapackage.Only/9.9.9": { "type": "package", "serviceable": true }
            """,
        string meshWeaverAssemblyVersion = "3.0.0.0",
        string? secondMeshWeaverAssemblyVersion = null) => $$"""
        {
          "runtimeTarget": { "name": "net10.0" },
          "targets": {
            "net10.0": {
              "Host/1.0.0": {},
              "Some.Package/4.2.0": {
                "runtime": { "lib/net10.0/Some.Package.dll": { "assemblyVersion": "4.2.0.0" } }
              },
              "Renamed.Package/1.0.0": {
                "runtime": { "lib/net10.0/Renamed.Assembly.dll": { "assemblyVersion": "1.0.0.0" } }
              },
              "Metapackage.Only/9.9.9": {},
              "MeshWeaver.Data/3.0.0": {
                "runtime": { "MeshWeaver.Data.dll": { "assemblyVersion": "{{meshWeaverAssemblyVersion}}" } }
              }{{(secondMeshWeaverAssemblyVersion is null ? "" : $$"""
              ,
              "MeshWeaver.Layout/3.0.0": {
                "runtime": { "MeshWeaver.Layout.dll": { "assemblyVersion": "{{secondMeshWeaverAssemblyVersion}}" } }
              }
              """)}}
            }
          },
          "libraries": {
            "Host/1.0.0": { "type": "project" },
            {{packages}}
          }
        }
        """;

    private static void Dll(string directory, string name) =>
        File.WriteAllBytes(Path.Combine(directory, name + ".dll"), [0x4D, 0x5A]);

    // ── fail closed ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AMissingAppDirectoryIsRefusedRatherThanTreatedAsEmpty()
    {
        Action act = () => ContainerReferenceSet.Read(Path.Combine(_root, "nope"), string.Empty);

        act.Should().Throw<ContainerReferenceSet.UnreadableContainerException>()
            .Which.Message.Should().Contain("does not exist");
    }

    [Fact]
    public void AnAppDirectoryWithNoAssembliesIsAFailure()
    {
        var app = App("empty");

        Action act = () => ContainerReferenceSet.Read(app, string.Empty);

        act.Should().Throw<ContainerReferenceSet.UnreadableContainerException>()
            .Which.Message.Should().Contain("holds no assemblies");
    }

    [Fact]
    public void NoDepsJsonMeansTheVersionsAndTheBindingIdentityCannotBeRead()
    {
        var app = App("no-deps");
        Dll(app, "MeshWeaver.Data");

        Action act = () => ContainerReferenceSet.Read(app, string.Empty);

        act.Should().Throw<ContainerReferenceSet.UnreadableContainerException>()
            .Which.Message.Should().Contain("exactly one *.deps.json");
    }

    [Fact]
    public void TwoDepsJsonFilesAreAmbiguousAndRefused()
    {
        var app = App("two-deps");
        Dll(app, "MeshWeaver.Data");
        File.WriteAllText(Path.Combine(app, "a.deps.json"), Deps());
        File.WriteAllText(Path.Combine(app, "b.deps.json"), Deps());

        Action act = () => ContainerReferenceSet.Read(app, string.Empty);

        act.Should().Throw<ContainerReferenceSet.UnreadableContainerException>()
            .Which.Message.Should().Contain("found 2");
    }

    [Fact]
    public void MeshWeaverAssembliesThatDisagreeOnTheirBindingIdentityStopTheRun()
    {
        // 🚨 MeshWeaver#143's failure, caught in the image instead of at run time as a
        // FileNotFoundException naming a version nobody wrote down.
        var app = App("split-identity");
        Dll(app, "MeshWeaver.Data");
        File.WriteAllText(Path.Combine(app, "host.deps.json"),
            Deps(secondMeshWeaverAssemblyVersion: "2.0.0.0"));

        Action act = () => ContainerReferenceSet.Read(app, string.Empty);

        act.Should().Throw<ContainerReferenceSet.UnreadableContainerException>()
            .Which.Message.Should().Contain("do not agree on a binding identity");
    }

    [Fact]
    public void ADepsJsonThatListsNoPackagesResolvesNothingAndSaysSo()
    {
        var app = App("no-packages");
        Dll(app, "MeshWeaver.Data");
        File.WriteAllText(Path.Combine(app, "host.deps.json"), """
            {
              "targets": { "net10.0": { "Host/1.0.0": {} } },
              "libraries": { "Host/1.0.0": { "type": "project" } }
            }
            """);

        Action act = () => ContainerReferenceSet.Read(app, string.Empty);

        act.Should().Throw<ContainerReferenceSet.UnreadableContainerException>()
            .Which.Message.Should().Contain("lists no packages");
    }

    // ── the matching rule ──────────────────────────────────────────────────────────────────────

    private ContainerReferenceSet ReadHealthyApp()
    {
        var app = App("healthy");
        Dll(app, "MeshWeaver.Data");
        Dll(app, "Some.Package");
        Dll(app, "Renamed.Assembly");
        File.WriteAllText(Path.Combine(app, "host.deps.json"), Deps());
        return ContainerReferenceSet.Read(app, string.Empty);
    }

    [Fact]
    public void APackageWhoseAssemblyIsOnDiskResolvesToThatFile()
    {
        var refs = ReadHealthyApp();

        var resolution = refs.Resolve("Some.Package");

        resolution.Supplied.Should().BeTrue();
        resolution.Version.Should().Be("4.2.0");
        resolution.AssemblyPaths.Should().ContainSingle()
            .Which.Should().Contain("Some.Package.dll");
    }

    [Fact]
    public void APackageWhoseASSEMBLYIsNamedDifferentlyStillResolves()
    {
        // Matched through the image's own deps.json, which says which files the package
        // contributes — id-equals-file-stem alone would call this one missing.
        var refs = ReadHealthyApp();

        var resolution = refs.Resolve("Renamed.Package");

        resolution.Supplied.Should().BeTrue();
        resolution.AssemblyPaths.Should().ContainSingle().Which.Should().Contain("Renamed.Assembly.dll");
    }

    [Fact]
    public void AMetapackageWithNoAssemblyIsNOTSupplied_EvenThoughTheImageKnowsItsVersion()
    {
        // 🚨 The whole point of matching by the file: the image was BUILT with this package and
        // records its version, but it contributes no assembly, so treating the version as proof
        // would let a build lose a type it needs.
        var refs = ReadHealthyApp();

        var resolution = refs.Resolve("Metapackage.Only");

        resolution.Version.Should().Be("9.9.9");
        resolution.Supplied.Should().BeFalse();
        resolution.AssemblyPaths.Should().BeEmpty();
    }

    [Fact]
    public void APackageTheImageHasNeverHeardOfIsAnAdditionalLibrary()
    {
        var refs = ReadHealthyApp();

        var resolution = refs.Resolve("ClosedXML");

        resolution.Supplied.Should().BeFalse();
        resolution.Version.Should().BeNull();
    }

    [Fact]
    public void TheSharedFrameworkArrivesThroughTheProcessTpaRatherThanFromAppOnDisk()
    {
        // The portal image's /app OMITS the shared-framework assemblies; they reach a compile only
        // through TRUSTED_PLATFORM_ASSEMBLIES. A reference set that read only /app would fail
        // CS0012 on ILogger — measured on all 48 Plugins modules at once.
        var app = App("tpa");
        Dll(app, "MeshWeaver.Data");
        File.WriteAllText(Path.Combine(app, "host.deps.json"), Deps());
        var framework = typeof(object).Assembly.Location;

        var refs = ContainerReferenceSet.Read(app, framework);

        refs.FindAssembly(Path.GetFileNameWithoutExtension(framework)).Should().Be(framework);
        refs.PlatformAssemblyVersion.Should().Be("3.0.0.0");
    }

    [Fact]
    public void TheREALHostDepsJsonOfThisBuildIsReadable_NotJustTheSyntheticOne()
    {
        // The fixtures above are hand-written; this one is the deps.json mw-plugin-test actually
        // SHIPS — 73 packages, the MeshWeaver assemblies, one binding identity. If the parser can
        // only read the fixtures, it cannot read a container.
        var app = App("real");
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "mw-plugin-test.deps.json"),
            Path.Combine(app, "mw-plugin-test.deps.json"));
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "MeshWeaver.ShortGuid.dll"),
            Path.Combine(app, "MeshWeaver.ShortGuid.dll"));

        var refs = ContainerReferenceSet.Read(app);

        refs.PackageVersions.Count.Should().BeGreaterThan(50);
        refs.PlatformAssemblyVersion.Should().NotBeNullOrEmpty();
        // The shared framework and the rest of the closure arrive through the process's TPA.
        refs.AssembliesByName.Count.Should().BeGreaterThan(50);
        refs.FindAssembly("MeshWeaver.Data").Should().NotBeNull();
    }

    [Fact]
    public void TheSharedFRAMEWORKsOfTheContainerAreInTheReferenceSet()
    {
        // 🚨 TPA is the BUILDER's closure, and the builder is not a web app — so ASP.NET Core's
        // assemblies reach the compile only because the shared-framework directories are scanned.
        // Measured: 15 of 51 Plugins modules reported Microsoft.AspNetCore.Components.Web "not
        // supplied" against an image that ships it, before this existed.
        var app = App("frameworks");
        Dll(app, "MeshWeaver.Data");
        File.WriteAllText(Path.Combine(app, "host.deps.json"), Deps());

        var refs = ContainerReferenceSet.Read(app, string.Empty);

        // System.Runtime comes from Microsoft.NETCore.App, which every .NET install has; the TPA
        // argument is empty, so finding it at all proves the shared-framework scan ran.
        refs.FindAssembly("System.Runtime").Should().NotBeNull();
        refs.FindAssembly("System.Runtime")!.Should().Contain("Microsoft.NETCore.App");
    }

    [Fact]
    public void AnAmbiguousBinDirectoryIsRefused_WhichIsWhyTheAppDirectoryIsACOPY()
    {
        // A TEST output directory carries three *.deps.json (the suite's, mw-plugin-test's,
        // mw-combo-verify's). Refusing it is the fail-closed rule doing its job — recorded here so
        // nobody "fixes" the refusal by picking the first one.
        Action act = () => ContainerReferenceSet.Read(AppContext.BaseDirectory);

        act.Should().Throw<ContainerReferenceSet.UnreadableContainerException>()
            .Which.Message.Should().Contain("exactly one *.deps.json");
    }
}
