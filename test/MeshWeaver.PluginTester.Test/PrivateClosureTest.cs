using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// What a container-built module BUNDLES, as opposed to what it COMPILED against — the accounting
/// MeshWeaver.Plugins#1043 got wrong.
///
/// <para>The container flip made "the reference image has this file" mean "the platform supplies
/// it, so the bundle need not". The reference image is a PORTAL, and a portal with a module
/// compiled into it carries that module's private package dependencies too — so
/// <c>Microsoft.Agents.AI</c> read as platform-supplied, left the <c>MeshWeaver.AI</c> bundle, and
/// every host that was not that exact image (the bake gate, MeshWeaver.Reinsurance's trunk seeding
/// the published Store bundle) threw <c>ReflectionTypeLoadException</c>.</para>
///
/// <para>These tests pin the replacement rule, which is the SDK path's rule: the module's own
/// package closure rides, and the SHARED FRAMEWORK is the only sound omission.</para>
/// </summary>
public class PrivateClosureTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mw-private-closure-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static void Dll(string directory, string name) =>
        File.WriteAllBytes(Path.Combine(directory, name + ".dll"), [0x4D, 0x5A]);

    /// <summary>
    /// A PORTAL image: it carries the platform AND a module compiled into it, so its <c>/app</c>
    /// holds that module's private dependency (<c>Provider.Sdk</c> → <c>Provider.Transitive</c>)
    /// exactly as <c>memex-portal-ai</c> holds <c>Microsoft.Agents.AI</c>.
    /// </summary>
    private ContainerReferenceSet Portal()
    {
        var app = Path.Combine(_root, "app");
        Directory.CreateDirectory(app);
        foreach (var name in new[]
                 { "MeshWeaver.Data", "Provider.Sdk", "Provider.Transitive", "Platform.Lib" })
            Dll(app, name);
        File.WriteAllText(Path.Combine(app, "Portal.deps.json"), """
            {
              "runtimeTarget": { "name": "net10.0" },
              "targets": {
                "net10.0": {
                  "Portal/1.0.0": {},
                  "Provider.Sdk/2.0.0": {
                    "runtime": { "lib/net10.0/Provider.Sdk.dll": { "assemblyVersion": "2.0.0.0" } },
                    "dependencies": { "Provider.Transitive": "1.0.0", "Framework.Riding.Package": "10.0.0" }
                  },
                  "Provider.Transitive/1.0.0": {
                    "runtime": { "lib/net10.0/Provider.Transitive.dll": { "assemblyVersion": "1.0.0.0" } }
                  },
                  "Framework.Riding.Package/10.0.0": {
                    "runtime": { "lib/net10.0/System.Framework.Thing.dll": { "assemblyVersion": "10.0.0.0" } }
                  },
                  "Platform.Lib/3.0.0": {
                    "runtime": { "lib/net10.0/Platform.Lib.dll": { "assemblyVersion": "3.0.0.0" } }
                  },
                  "MeshWeaver.Data/3.0.0": {
                    "runtime": { "MeshWeaver.Data.dll": { "assemblyVersion": "3.0.0.0" } }
                  }
                }
              },
              "libraries": {
                "Portal/1.0.0": { "type": "project" },
                "Provider.Sdk/2.0.0": { "type": "package" },
                "Provider.Transitive/1.0.0": { "type": "package" },
                "Framework.Riding.Package/10.0.0": { "type": "package" },
                "Platform.Lib/3.0.0": { "type": "package" },
                "MeshWeaver.Data/3.0.0": { "type": "package" }
              }
            }
            """);

        // A shared framework, laid out the way a runtime is: <root>/<Framework>/<version>/*.dll.
        var shared = Path.Combine(_root, "shared", "Microsoft.AspNetCore.App", "10.0.0");
        Directory.CreateDirectory(shared);
        Dll(shared, "System.Framework.Thing");

        return ContainerReferenceSet.Read(
            app, trustedPlatformAssemblies: string.Empty,
            sharedFrameworksRoot: Path.Combine(_root, "shared"));
    }

    /// <summary>A shelf carrying one package the portal image does NOT have.</summary>
    private ModuleLibrariesShelf Shelf()
    {
        var dir = Path.Combine(_root, "module-libs");
        Directory.CreateDirectory(dir);
        Dll(dir, "Additional.Lib");
        File.WriteAllText(Path.Combine(dir, "MeshWeaver.ModuleLibraries.deps.json"), """
            {
              "targets": {
                "net10.0": {
                  "Additional.Lib/5.0.0": {
                    "runtime": { "lib/net10.0/Additional.Lib.dll": {} }
                  }
                }
              }
            }
            """);
        return ModuleLibrariesShelf.Read(dir);
    }

    [Fact]
    public void APackageTheREFERENCEIMAGECarriesStillRides()
    {
        var closure = PrivateClosure.Derive(["Provider.Sdk"], Portal(), shelf: null);

        closure.Rides.Select(r => r.AssemblyName).Should()
            .Contain("Provider.Sdk",
                "the image holds it only because a module was compiled into that portal — reading "
                + "that as a platform guarantee is what dropped Microsoft.Agents.AI out of the AI "
                + "bundle and broke every consumer that was not that image (Plugins#1043)")
            .And.Contain("Provider.Transitive",
                "the SDK hands a consumer the package's TRANSITIVE closure, and a bundle that "
                + "stops at the direct reference faults one hop later instead of at first use");
        closure.Missing.Should().BeEmpty();
    }

    [Fact]
    public void TheSharedFrameworkIsTheOneSoundOmission()
    {
        var closure = PrivateClosure.Derive(["Provider.Sdk"], Portal(), shelf: null);

        closure.Rides.Select(r => r.AssemblyName).Should().NotContain("System.Framework.Thing");
        closure.FrameworkResolved.Should().Contain("System.Framework.Thing",
            "a shared framework travels with every host that can load the module at all, so it is "
            + "the only thing a bundle may leave to the consumer — recorded, never merely dropped");
    }

    [Fact]
    public void ThePlatformsOwnAssembliesNeverRide()
    {
        var closure = PrivateClosure.Derive(["MeshWeaver.Data", "Provider.Sdk"], Portal(), shelf: null);

        closure.Rides.Select(r => r.AssemblyName).Should().NotContain("MeshWeaver.Data",
            "MeshWeaver.* reaches a module as a ProjectReference and binds by a strict "
            + "AssemblyVersion — a bundled copy is the #143 same-identity trap");
    }

    [Fact]
    public void TheShelfSuppliesWhatTheImageDoesNotCarry()
    {
        var closure = PrivateClosure.Derive(["Additional.Lib"], Portal(), Shelf());

        closure.Rides.Should().ContainSingle()
            .Which.Source.Should().Be("the shelf",
                "an ADDITIONAL library is by definition one the image does not have");
        closure.Missing.Should().BeEmpty();
    }

    [Fact]
    public void TheIMAGEsBytesWinWhenBothHaveIt_soTheRideIsWhatTheCompileBound()
    {
        var shelf = Shelf();
        File.WriteAllBytes(Path.Combine(shelf.Directory, "Provider.Sdk.dll"), [0x4D, 0x5A]);

        var closure = PrivateClosure.Derive(["Provider.Sdk"], Portal(), ModuleLibrariesShelf.Read(shelf.Directory));

        closure.Rides.Single(r => r.AssemblyName == "Provider.Sdk").Source.Should().Be("the image",
            "the compile referenced /app's copy (the container wins in the reference set), so the "
            + "bundle must carry those same bytes rather than a second opinion");
    }

    [Fact]
    public void AnAssemblyNeitherSourceHasIsREPORTED_neverSilentlyDropped()
    {
        var closure = PrivateClosure.Derive(["Nowhere.At.All"], Portal(), shelf: null);

        closure.Rides.Should().BeEmpty();
        closure.Missing.Should().Equal(["Nowhere.At.All"],
            "a package that compiled but cannot be materialized is the shape of a bundle that "
            + "faults at first use — the builder says so rather than packing silence");
    }
}
