using MeshWeaver.Plugin.Build;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// THE check that was missing. Converting a module from <c>"build": "sdk"</c> to
/// <c>"build": "container"</c> changes which compiler produces the bytes — it must not change what
/// the bundle CONTAINS. Nothing enforced that, so the flip silently emptied eight bundles'
/// closures and only surfaced as <c>ReflectionTypeLoadException</c> in other repositories'
/// trunks (MeshWeaver.Plugins#1043).
///
/// <para>The two lanes derive the closure from different records — the SDK from the module's own
/// publish <c>deps.json</c> (<see cref="DepsClosure"/>), the container from the image's and the
/// shelf's (<see cref="PrivateClosure"/>) — so this describes ONE package graph both ways and
/// holds the answers against each other. A future divergence fails here rather than in a satellite
/// repo's bake gate.</para>
/// </summary>
public class LaneClosuresAgreeTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mw-lane-closure-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private const string Module = "Widget.Module";

    /// <summary>
    /// One package graph, as the module's OWN publish records it: a direct provider package with a
    /// transitive, a framework-provided package, and a platform reference the walk stops at.
    /// </summary>
    private const string ModuleDepsJson = """
        {
          "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
          "targets": {
            ".NETCoreApp,Version=v10.0": {
              "Widget.Module/1.0.0": {
                "dependencies": { "Provider.Sdk": "2.0.0", "MeshWeaver.Data": "3.0.0" },
                "runtime": { "Widget.Module.dll": {} }
              },
              "Provider.Sdk/2.0.0": {
                "dependencies": { "Provider.Transitive": "1.0.0", "Framework.Riding.Package": "10.0.0" },
                "runtime": { "lib/net10.0/Provider.Sdk.dll": {} }
              },
              "Provider.Transitive/1.0.0": {
                "runtime": { "lib/net10.0/Provider.Transitive.dll": {} }
              },
              "Framework.Riding.Package/10.0.0": {
                "runtime": { "lib/net10.0/System.Framework.Thing.dll": {} }
              },
              "MeshWeaver.Data/3.0.0": {
                "dependencies": { "Platform.Only.Lib": "7.0.0" },
                "runtime": { "MeshWeaver.Data.dll": { "assemblyVersion": "3.0.0.0" } }
              },
              "Platform.Only.Lib/7.0.0": {
                "runtime": { "lib/net10.0/Platform.Only.Lib.dll": {} }
              }
            }
          },
          "libraries": {
            "Widget.Module/1.0.0": { "type": "project" },
            "Provider.Sdk/2.0.0": { "type": "package" },
            "Provider.Transitive/1.0.0": { "type": "package" },
            "Framework.Riding.Package/10.0.0": { "type": "package" },
            "MeshWeaver.Data/3.0.0": { "type": "package" },
            "Platform.Only.Lib/7.0.0": { "type": "package" }
          }
        }
        """;

    /// <summary>The SAME graph as a PORTAL image would carry it: every assembly on disk in /app,
    /// including the ones only there because a module was compiled into that portal.</summary>
    private ContainerReferenceSet Image()
    {
        var app = Path.Combine(_root, "app");
        Directory.CreateDirectory(app);
        foreach (var name in new[]
                 {
                     "MeshWeaver.Data", "Provider.Sdk", "Provider.Transitive", "Platform.Only.Lib",
                     "System.Framework.Thing",
                 })
            File.WriteAllBytes(Path.Combine(app, name + ".dll"), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(app, "Portal.deps.json"), ModuleDepsJson
            .Replace("\"Widget.Module/1.0.0\": {", "\"Portal/1.0.0\": {", StringComparison.Ordinal)
            .Replace("\"runtime\": { \"Widget.Module.dll\": {} }", "\"runtime\": { \"Portal.dll\": {} }",
                StringComparison.Ordinal));

        var shared = Path.Combine(_root, "shared", "Microsoft.AspNetCore.App", "10.0.0");
        Directory.CreateDirectory(shared);
        File.WriteAllBytes(Path.Combine(shared, "System.Framework.Thing.dll"), [0x4D, 0x5A]);

        return ContainerReferenceSet.Read(
            app, trustedPlatformAssemblies: string.Empty,
            sharedFrameworksRoot: Path.Combine(_root, "shared"));
    }

    [Fact]
    public void BothLanesDeriveTheSameNonFrameworkClosure()
    {
        var container = Image();

        // The SDK lane: the module's own publish deps.json, minus what the shared framework
        // supplies — the packer expresses that trim as "the file is absent from the publish
        // folder", which is the same set by a different route.
        var sdk = DepsClosure.Derive(ModuleDepsJson, Module)
            .Files
            .Where(f => !container.IsFrameworkSupplied(Path.GetFileNameWithoutExtension(f)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // The container lane: the module's declared package references walked over the image's
        // record.
        var containerLane = PrivateClosure.Derive(["Provider.Sdk"], container, shelf: null)
            .Rides
            .Select(r => r.AssemblyName + ".dll")
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        containerLane.Should().Equal(sdk,
            "the compiler that produced the bytes must not change what the bundle carries — the "
            + "container flip made 'the image has it' mean 'the bundle need not', and eight "
            + "packages shipped without their own closure (Plugins#1043)");
        sdk.Should().Equal(["Provider.Sdk.dll", "Provider.Transitive.dll"],
            "the module's own package closure rides, the shared framework does not, and the "
            + "platform reference's private dependency (Platform.Only.Lib) is /app's business");
    }

    [Fact]
    public void TheContainerLaneCarriesWhatTheImageAlreadyHas()
    {
        var container = Image();

        var rides = PrivateClosure.Derive(["Provider.Sdk"], container, shelf: null)
            .Rides.Select(r => r.AssemblyName).ToArray();

        rides.Should().Contain("Provider.Sdk",
            "every one of these files IS in the image — that is the whole trap: a portal that has "
            + "a module compiled into it carries the module's private dependencies, so treating "
            + "/app as a platform guarantee publishes a bundle only that portal can load");
    }
}
