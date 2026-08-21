using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The storage-backend contract gate (#1752). Cosmos and Snowflake are <b>bootstrap tier</b>: the
/// mesh cannot read itself without a storage backend, so they can never be Store-installed (the
/// Store's catalog lives behind the very storage you would be installing) and must ride the image
/// as a <c>modules/&lt;Name&gt;/</c> closure. What was missing when #1727 put them on that lane is any
/// assertion that the laid-out folder is <i>loadable</i>:
/// <list type="bullet">
///   <item>the compiler proves the source binds while the source is in-tree — but it says nothing
///     about the publish LAYOUT, and the closure lane's own prune is what can break it
///     ("a thin-lane row would land an assembly that fails to load its own driver at first use");</item>
///   <item>and the emulator suites cannot stand in for it: both fixtures green-SKIP when their
///     backend is unreachable, so they pass by not running.</item>
/// </list>
/// So this asserts exactly the seam a portal walks and nothing more — no emulator, no network,
/// milliseconds: <c>ResolveModulePath</c> finds the module under <c>modules/</c>, its private
/// driver rode the prune, <c>InstallAssemblies</c> folds the assembly's
/// <see cref="MeshNodeProviderAttribute"/>, and the keyed <see cref="IStorageAdapterFactory"/> that
/// <c>Graph:Storage:Type</c> resolves lands from THAT DLL. This project holds no compiled reference
/// to either backend, which is the whole claim under test.
///
/// <para>It is also the gate that has to stand before the source could move to a satellite repo:
/// point it at a pinned released binary instead of the in-tree build and it answers the question a
/// moved backend raises — does a module built against platform N−1 still bind in N.</para>
/// </summary>
public class StorageModuleLayoutTest
{
    /// <summary>
    /// module name · the PRIVATE driver assembly that must ride the closure. The driver is named
    /// rather than inferred on purpose: its presence in the module folder is the single thing the
    /// prune can take away, and swapping a backend's driver should be a deliberate edit here, not
    /// a silently weakened assertion.
    /// </summary>
    public static TheoryData<string, string> Layouts() => new()
    {
        { "MeshWeaver.Hosting.Cosmos", "Microsoft.Azure.Cosmos.Client.dll" },
        { "MeshWeaver.Hosting.Snowflake", "Snowflake.Data.dll" },
    };

    /// <summary>
    /// module name · the <c>Graph:Storage:Type</c> value · the factory that value must resolve to.
    /// </summary>
    public static TheoryData<string, string, string> Backends() => new()
    {
        { "MeshWeaver.Hosting.Cosmos", "Cosmos", "MeshWeaver.Hosting.Cosmos.CosmosStorageAdapterFactory" },
        { "MeshWeaver.Hosting.Snowflake", "Snowflake", "MeshWeaver.Hosting.Snowflake.SnowflakeStorageAdapterFactory" },
    };

    /// <summary>
    /// The bits actually ship: <c>ResolveModulePath</c> lands inside <c>modules/&lt;Name&gt;/</c> — not on
    /// its app-folder fallback, which would mean a ships-the-bits reference came back — and the
    /// module's private driver survived the prune and loads.
    /// </summary>
    [Theory]
    [MemberData(nameof(Layouts))]
    public void ClosureLane_LaysTheBackendOut_WithItsPrivateDriver(
        string moduleName, string driverAssemblyFile)
    {
        var expectedFolder = Path.Combine(AppContext.BaseDirectory, "modules", moduleName);
        var resolved = MeshBuilder.ResolveModulePath(moduleName + ".dll");

        // ResolveModulePath falls back to the app folder when modules/<Name>/<Name>.dll is absent,
        // so "the file exists" is NOT the assertion — WHERE it resolved is.
        Assert.True(
            File.Exists(resolved),
            $"{moduleName}: the closure lane laid out nothing loadable — ResolveModulePath returned '{resolved}', which does not exist.");
        Assert.Equal(Path.Combine(expectedFolder, moduleName + ".dll"), resolved);

        var driver = Path.Combine(expectedFolder, driverAssemblyFile);
        Assert.True(
            File.Exists(driver),
            $"{moduleName}: '{driverAssemblyFile}' is missing from {expectedFolder}. The backend's driver exists nowhere else — no host references it — so a portal selecting this backend would fault at first use. Check the closure lane's prune in memex/MeshModulesPublish.targets.");
        Assert.NotNull(Assembly.LoadFrom(driver));
    }

    /// <summary>
    /// module name · the RID · the LOADABLE native the backend calls into. Issue #1728: the closure
    /// lane's prune used to delete <c>runtimes/</c> outright, on the reasoning that
    /// <c>Assembly.LoadFrom</c> never consults a module's deps.json — true, and the reason nothing
    /// could ever find the payload. Both backends on this lane genuinely ship natives, so that prune
    /// shipped two storage backends that fault at their first native call, in exactly the two
    /// suites that green-SKIP when their emulator is unreachable.
    /// </summary>
    public static TheoryData<string, string, string> Natives() => new()
    {
        // Snowflake.Data P/Invokes its own mini-core; Mono.Unix rides with it.
        { "MeshWeaver.Hosting.Snowflake", "linux-x64", "libsf_mini_core.so" },
        { "MeshWeaver.Hosting.Snowflake", "linux-arm64", "libMono.Unix.so" },
        // Cosmos' ServiceInterop is a native win-x64 DLL (query plan evaluation).
        { "MeshWeaver.Hosting.Cosmos", "win-x64", "Microsoft.Azure.Cosmos.ServiceInterop.dll" },
    };

    /// <summary>
    /// The native payload rides the closure, under the RID tree the host resolves at load time
    /// (<c>ModuleNativeAssets</c>). Asserted per (module, RID, file) rather than by counting the
    /// tree: a count would pass on any surviving RID, and the RID a deployment needs is the one
    /// that must be there.
    /// </summary>
    [Theory]
    [MemberData(nameof(Natives))]
    public void ClosureLane_KeepsTheBackendsLoadableNatives(
        string moduleName, string runtimeIdentifier, string nativeFile)
    {
        var expected = Path.Combine(
            AppContext.BaseDirectory, "modules", moduleName,
            "runtimes", runtimeIdentifier, "native", nativeFile);
        Assert.True(
            File.Exists(expected),
            $"{moduleName}: '{nativeFile}' is missing from runtimes/{runtimeIdentifier}/native. A module's "
            + "native payload exists nowhere else, and ModuleNativeAssets resolves it from exactly this "
            + "path — so a deployment selecting this backend would fault at its first P/Invoke. Check "
            + "prune (0) in memex/MeshModulesPublish.targets (#1728).");
    }

    /// <summary>
    /// The directory the host probes is the directory the lane wrote. Pins the two halves of #1728
    /// to each other: a prune that keeps a tree the resolver never looks in, or a resolver that
    /// looks where the prune does not keep, would each pass its own test alone.
    ///
    /// <para>Compared as a DIRECTORY, not a file name: the decorated file forms are per-platform
    /// (<c>libX.so</c> / <c>libX.dylib</c> / <c>X.dll</c>, covered by <c>ModuleNativeAssetTest</c>),
    /// and a win-x64 payload is correctly unresolvable on Linux. The layout claim is not.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Natives))]
    public void TheResolverProbesExactlyWhereTheLaneWrote(
        string moduleName, string runtimeIdentifier, string nativeFile)
    {
        var moduleDirectory = Path.Combine(AppContext.BaseDirectory, "modules", moduleName);
        var laidOut = Path.GetDirectoryName(Path.Combine(
            moduleDirectory, "runtimes", runtimeIdentifier, "native", nativeFile));

        var probed = ModuleNativeAssets
            .CandidatePaths(moduleDirectory, "probe", runtimeIdentifier)
            .Select(Path.GetDirectoryName)
            .ToList();
        Assert.Contains(laidOut, probed);
    }

    /// <summary>
    /// The seam binds: loading the module the way <c>Modules:Assemblies</c> does registers the keyed
    /// factory <c>Graph:Storage:Type</c> resolves, and the registration comes from the module folder
    /// DLL. Then <c>Create</c> runs far enough to JIT against the platform's contract types and its
    /// own driver, refusing on configuration (<see cref="InvalidOperationException"/>) rather than
    /// on a loader failure — with no endpoint, no container and no network.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backends))]
    public void Portal_WithNoCompiledReference_ResolvesTheKeyedFactory(
        string moduleName, string storageKey, string factoryTypeName)
    {
        var modulePath = MeshBuilder.ResolveModulePath(moduleName + ".dll");
        var services = Install(modulePath);

        var descriptor = Assert.Single(
            services,
            d => d.ServiceType == typeof(IStorageAdapterFactory) && Equals(d.ServiceKey, storageKey));
        var implementation = descriptor.KeyedImplementationType;
        Assert.NotNull(implementation);
        Assert.Equal(factoryTypeName, implementation.FullName);

        // The registration must come from the DLL under modules/ — an app-root copy resolving here
        // would make every other assertion true of the wrong bytes.
        Assert.Equal(modulePath, implementation.Assembly.Location);

        var provider = services.AddOptions().BuildServiceProvider();
        var factory = provider.GetRequiredKeyedService<IStorageAdapterFactory>(storageKey);

        // Unconfigured, both backends refuse with InvalidOperationException. Reaching that refusal
        // means the method JITted: its contract types (GraphStorageConfig, IStorageAdapter) bound
        // against the platform, and its driver types bound against the module folder. A missing or
        // mismatched dependency surfaces here as a loader exception instead, naming what broke.
        Assert.Throws<InvalidOperationException>(
            () => factory.Create(new GraphStorageConfig { Type = storageKey }, provider));
    }

    /// <summary>
    /// The portal's own fold: <see cref="MeshBuilder.InstallAssemblies"/> is what reads
    /// <c>Modules:Assemblies</c> at boot — <c>Assembly.LoadFrom</c> plus attribute discovery.
    /// </summary>
    private static IServiceCollection Install(string modulePath)
    {
        var serviceConfigs = new List<Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => serviceConfigs.Add(configure), AddressExtensions.CreateMeshAddress());
        builder.InstallAssemblies(modulePath);
        return serviceConfigs.Aggregate(
            (IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));
    }
}
