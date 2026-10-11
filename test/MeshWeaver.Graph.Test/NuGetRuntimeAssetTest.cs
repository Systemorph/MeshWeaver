using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MeshWeaver.NuGet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NuGet.Frameworks;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Offline pins for how a NodeType's NuGet packages bind at RUN time (no feed needed):
/// <list type="bullet">
///   <item>a package's <c>runtimes/&lt;rid&gt;/lib/&lt;tfm&gt;/</c> group replaces its <c>lib/&lt;tfm&gt;/</c>
///     group — the shape of every .NET SQL Server client, whose <c>lib/</c> copy is the "not supported
///     on this platform" stub (partnerre.meshweaver.cloud, linux-x64, 2026-10-11);</item>
///   <item>a dependency the host ships at an equal-or-newer version binds to the host's copy.</item>
/// </list>
/// </summary>
public class NuGetRuntimeAssetTest : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mw-nuget-rid-" + Guid.NewGuid().ToString("N"));

    public NuGetRuntimeAssetTest() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string[] Layout(params string[] relativeFiles)
    {
        foreach (var rel in relativeFiles)
        {
            var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, rel);
        }
        return relativeFiles;
    }

    private static readonly NuGetFramework Net10 = NuGetFramework.Parse("net10.0");

    [Fact]
    public void RidFallbackChain_LinuxX64_FallsBackThroughLinuxAndUnix()
        => RuntimeAssetSelector.RidFallbackChain("linux-x64").Should()
            .Equal("linux-x64", "linux", "unix-x64", "unix", "any");

    [Fact]
    public void RidFallbackChain_CoversMuslMacWindowsAndDistroRids()
    {
        RuntimeAssetSelector.RidFallbackChain("linux-musl-x64").Should()
            .Equal("linux-musl-x64", "linux-musl", "linux-x64", "linux", "unix-x64", "unix", "any");
        RuntimeAssetSelector.RidFallbackChain("osx-arm64").Should()
            .Equal("osx-arm64", "osx", "unix-arm64", "unix", "any");
        RuntimeAssetSelector.RidFallbackChain("win-x64").Should().Equal("win-x64", "win", "any");
        RuntimeAssetSelector.RidFallbackChain("ubuntu.22.04-x64").Should()
            .Equal("ubuntu.22.04-x64", "ubuntu.22.04", "linux-x64", "linux", "unix-x64", "unix", "any");
    }

    [Fact]
    public void Select_SqlClientShape_LoadsTheUnixRuntimeAssetAndCompilesAgainstLib()
    {
        // Microsoft.Data.SqlClient 6.1.1's layout: lib/ is the stub, runtimes/{unix,win}/lib the real thing.
        var files = Layout(
            "lib/net8.0/Microsoft.Data.SqlClient.dll",
            "lib/net9.0/Microsoft.Data.SqlClient.dll",
            "lib/net9.0/de/Microsoft.Data.SqlClient.resources.dll",
            "ref/net9.0/Microsoft.Data.SqlClient.dll",
            "runtimes/win/lib/net9.0/Microsoft.Data.SqlClient.dll",
            "runtimes/unix/lib/net8.0/Microsoft.Data.SqlClient.dll",
            "runtimes/unix/lib/net9.0/Microsoft.Data.SqlClient.dll");

        var assets = RuntimeAssetSelector.Select(_root, files, Net10, RuntimeAssetSelector.RidFallbackChain("linux-x64"));

        assets.RuntimeRid.Should().Be("unix");
        assets.Runtime.Should().ContainSingle()
            .Which.Should().Be(Path.Combine(_root, "runtimes", "unix", "lib", "net9.0", "Microsoft.Data.SqlClient.dll"));
        assets.Compile.Should().ContainSingle()
            .Which.Should().Be(Path.Combine(_root, "lib", "net9.0", "Microsoft.Data.SqlClient.dll"));
        assets.Native.Should().BeEmpty();

        var onWindows = RuntimeAssetSelector.Select(_root, files, Net10, RuntimeAssetSelector.RidFallbackChain("win-x64"));
        onWindows.RuntimeRid.Should().Be("win");
        onWindows.Runtime.Should().ContainSingle().Which.Should().Contain(Path.Combine("runtimes", "win", "lib"));
    }

    [Fact]
    public void Select_PrefersTheNearestRid_AndPicksNativeAssets()
    {
        var files = Layout(
            "lib/net8.0/Pkg.dll",
            "runtimes/unix/lib/net8.0/Pkg.dll",
            "runtimes/linux-x64/lib/net8.0/Pkg.dll",
            "runtimes/linux-arm64/lib/net8.0/Pkg.dll",
            "runtimes/linux-x64/native/libpkg.so",
            "runtimes/win-x64/native/pkg.dll");

        var assets = RuntimeAssetSelector.Select(_root, files, Net10, RuntimeAssetSelector.RidFallbackChain("linux-x64"));

        assets.RuntimeRid.Should().Be("linux-x64");
        assets.Runtime.Should().ContainSingle().Which.Should().Contain(Path.Combine("runtimes", "linux-x64", "lib"));
        assets.Native.Should().ContainSingle().Which.Should().EndWith("libpkg.so");

        // The native probe finds a DllImport("pkg") as libpkg.so on Linux — and never probes a
        // native directory for a managed assembly.
        var nativeDir = Path.GetDirectoryName(assets.Native[0])!;
        PackageAssemblyProbe.IsNativeDirectory(nativeDir).Should().BeTrue();
        PackageAssemblyProbe.FindManaged("pkg", [nativeDir]).Should().BeNull();
        if (OperatingSystem.IsLinux())
            PackageAssemblyProbe.FindNative("pkg", [nativeDir]).Should().Be(assets.Native[0]);
    }

    [Fact]
    public void Select_PackageWithoutRuntimes_UsesLibForBoth()
    {
        var files = Layout("lib/netstandard2.0/Plain.dll", "lib/net462/Plain.dll");

        var assets = RuntimeAssetSelector.Select(_root, files, Net10, RuntimeAssetSelector.RidFallbackChain("linux-x64"));

        assets.RuntimeRid.Should().BeNull();
        assets.Compile.Should().Equal(assets.Runtime);
        assets.Runtime.Should().ContainSingle().Which.Should().Contain("netstandard2.0");
    }

    [Fact]
    public void Select_RuntimeOnlyPackage_CompilesAgainstItsRuntimeGroup()
    {
        var files = Layout("runtimes/unix/lib/net8.0/OnlyRid.dll", "runtimes/win/lib/net8.0/OnlyRid.dll");

        var assets = RuntimeAssetSelector.Select(_root, files, Net10, RuntimeAssetSelector.RidFallbackChain("linux-x64"));

        assets.Compile.Should().Equal(assets.Runtime);
        assets.Runtime.Should().ContainSingle().Which.Should().Contain(Path.Combine("runtimes", "unix"));
    }

    [Fact]
    public void Probe_AHostAssemblyInAPackageDirectory_BindsToTheHostsCopy()
    {
        // A package that ships the host's own System.Text.Json (an equal version — the boundary of
        // "equal-or-newer") must NOT load a second copy into the NodeType's context.
        var hostAssembly = typeof(JsonSerializer).Assembly;
        var packageDir = Path.Combine(_root, "pkg", "lib", "net8.0");
        Directory.CreateDirectory(packageDir);
        File.Copy(hostAssembly.Location, Path.Combine(packageDir, "System.Text.Json.dll"));

        var context = new ProbingContext([packageDir]);
        try
        {
            var bound = context.LoadFromAssemblyName(hostAssembly.GetName());

            bound.Should().BeSameAs(hostAssembly);
            context.Assemblies.Should().BeEmpty("the host's copy served the reference; nothing was loaded beside it");
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void Probe_APackageOnlyAssembly_LoadsFromThePackageDirectory()
    {
        var packageDir = Path.Combine(_root, "pkg2", "runtimes", "unix", "lib", "net8.0");
        Directory.CreateDirectory(packageDir);
        var name = "MwRidProbe" + Guid.NewGuid().ToString("N");
        var emit = CSharpCompilation.Create(name,
                [CSharpSyntaxTree.ParseText("public static class P { public static int V => 42; }")],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .Emit(Path.Combine(packageDir, name + ".dll"));
        emit.Success.Should().BeTrue();

        PackageAssemblyProbe.HostVersionOf(name).Should().BeNull();
        var context = new ProbingContext([packageDir]);
        try
        {
            var loaded = context.LoadFromAssemblyName(new AssemblyName(name));

            AssemblyLoadContext.GetLoadContext(loaded).Should().BeSameAs(context);
            loaded.Location.Should().Be(Path.Combine(packageDir, name + ".dll"));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>A load context that binds exactly the way a NodeType's context binds packages.</summary>
    internal sealed class ProbingContext(string[] probingDirectories)
        : AssemblyLoadContext("nuget-probe-test", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
            => PackageAssemblyProbe.Resolve(this, assemblyName, probingDirectories);

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
            => PackageAssemblyProbe.ResolveUnmanaged(unmanagedDllName, probingDirectories);
    }
}
