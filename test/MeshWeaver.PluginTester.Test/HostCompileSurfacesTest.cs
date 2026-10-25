#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeshWeaver.PluginTester;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// 🚨 <b>A bake against an older host of the same compatibility key compiles against THAT host's
/// compile surfaces.</b> The platform dropped System.Interactive, and this toolchain no longer embeds
/// a surface of its own. A still-running older image ships the package's implementation assembly
/// (which keeps <c>EnumerableEx.MaxBy</c> in namespace <c>System.Linq</c>) AND embeds the package's
/// reference assembly in its <c>MeshWeaver.Compiler.dll</c>. Its portals compile
/// <c>Enumerable.MaxBy</c> cleanly against that reference assembly. A bake that referenced the bare
/// implementation would go CS0121 on source those portals accept.
///
/// <para>The stand-in host is built here from scratch: an Ix-shaped implementation assembly, its
/// narrower reference assembly of the SAME identity, and an anchor <c>MeshWeaver.Compiler.dll</c>
/// that embeds the reference assembly under <see cref="HostCompileSurfaces.ResourcePrefix"/>. The
/// negative control compiles the same probe against the bare implementation and must see CS0121, so
/// the positive result cannot come from a probe that never reaches the ambiguity.</para>
/// </summary>
public class HostCompileSurfacesTest
{
    private const string PackageName = "Fake.Interactive";

    private const string ImplementationSource =
        """
        using System.Collections.Generic;

        namespace System.Linq
        {
            public static class EnumerableEx
            {
                public static IList<TSource> MaxBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector) => new List<TSource>();
            }
        }

        namespace Fake.Interactive
        {
            public static class Marker { public static int Value => 1; }
        }
        """;

    private const string ReferenceSource =
        """
        namespace Fake.Interactive
        {
            public static class Marker { public static int Value => 1; }
        }
        """;

    private const string ProbeSource =
        """
        using System.Linq;

        public static class MaxByProbe
        {
            public static int Run() => new[] { 3, 7, 5 }.ToList().MaxBy(v => v) + Fake.Interactive.Marker.Value;
        }
        """;

    [Fact]
    public void AHostEmbeddingASurface_CompilesTheBclCall_WhereItsBareImplementationIsAmbiguous()
    {
        var host = TempDirectory("mw-surface-host");
        try
        {
            var implementation = Path.Combine(host, PackageName + ".dll");
            File.WriteAllBytes(implementation, Emit(PackageName, "7.0.1.0", ImplementationSource));
            WriteAnchor(host, (PackageName + ".dll", Emit(PackageName, "7.0.1.0", ReferenceSource)));

            var surfaces = HostCompileSurfaces.OfHost(host);
            Assert.Equal([PackageName + ".dll"], surfaces.FileNames);

            var substituted = surfaces.ReferenceFor(implementation);
            var substitutedPath = Assert.IsAssignableFrom<PortableExecutableReference>(substituted).FilePath;
            Assert.NotEqual(implementation, substitutedPath);
            Assert.Empty(Errors(substituted));

            // Negative control: the bare implementation IS ambiguous, so the probe reaches the case.
            Assert.Contains(Errors(MetadataReference.CreateFromFile(implementation)), d => d.Id == "CS0121");
        }
        finally
        {
            Cleanup(host);
        }
    }

    [Fact]
    public void ASurfaceOfAnotherBuild_OrAHostWithoutSurfaces_LeavesTheHostFile()
    {
        var host = TempDirectory("mw-surface-other");
        var bare = TempDirectory("mw-surface-none");
        try
        {
            var implementation = Path.Combine(host, PackageName + ".dll");
            File.WriteAllBytes(implementation, Emit(PackageName, "7.0.2.0", ImplementationSource));
            // The embedded surface is 7.0.1.0, the shipped implementation 7.0.2.0: not the same identity.
            WriteAnchor(host, (PackageName + ".dll", Emit(PackageName, "7.0.1.0", ReferenceSource)));
            var otherBuild = HostCompileSurfaces.OfHost(host).ReferenceFor(implementation);
            Assert.Equal(implementation, Assert.IsAssignableFrom<PortableExecutableReference>(otherBuild).FilePath);

            // A host whose anchor embeds no surface (every host built after the package left).
            var bareImplementation = Path.Combine(bare, PackageName + ".dll");
            File.WriteAllBytes(bareImplementation, Emit(PackageName, "7.0.1.0", ImplementationSource));
            WriteAnchor(bare);
            var none = HostCompileSurfaces.OfHost(bare);
            Assert.Empty(none.FileNames);
            Assert.Equal(bareImplementation,
                Assert.IsAssignableFrom<PortableExecutableReference>(none.ReferenceFor(bareImplementation)).FilePath);
        }
        finally
        {
            Cleanup(host);
            Cleanup(bare);
        }
    }

    [Fact]
    public void AnUnreadableAnchor_Throws_RatherThanAnsweringNone()
    {
        var host = TempDirectory("mw-surface-corrupt");
        try
        {
            File.WriteAllText(Path.Combine(host, "MeshWeaver.Compiler.dll"), "not a PE image");
            Assert.ThrowsAny<BadImageFormatException>(() => HostCompileSurfaces.OfHost(host));
        }
        finally
        {
            Cleanup(host);
        }
    }

    private static IReadOnlyList<Diagnostic> Errors(MetadataReference package) =>
        CSharpCompilation.Create(
                "Probe_" + Guid.NewGuid().ToString("N"),
                [CSharpSyntaxTree.ParseText(ProbeSource)],
                [.. FrameworkReferences(), package],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

    /// <summary>The runtime's own framework assemblies, out of this process's TPA.</summary>
    private static IEnumerable<MetadataReference> FrameworkReferences()
    {
        var framework = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)
                        && string.Equals(Path.GetDirectoryName(p), framework, StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
    }

    private static byte[] Emit(string assemblyName, string version, string source,
        IEnumerable<ResourceDescription>? resources = null)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [
                CSharpSyntaxTree.ParseText(source),
                CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{version}\")]"),
            ],
            FrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream, manifestResources: resources,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emit.Success,
            $"{assemblyName} did not compile: "
            + string.Join("; ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return stream.ToArray();
    }

    /// <summary>A stand-in <c>MeshWeaver.Compiler.dll</c> embedding <paramref name="surfaces"/> the way
    /// hosts that shipped System.Interactive did.</summary>
    private static void WriteAnchor(string host, params (string FileName, byte[] Image)[] surfaces)
    {
        Directory.CreateDirectory(host);
        var resources = surfaces
            .Select(s => new ResourceDescription(
                HostCompileSurfaces.ResourcePrefix + s.FileName, () => new MemoryStream(s.Image), isPublic: true))
            .ToList();
        File.WriteAllBytes(Path.Combine(host, "MeshWeaver.Compiler.dll"),
            Emit("MeshWeaver.Compiler", "3.0.0.0", "namespace MeshWeaver.Compiler { internal static class Anchor { } }", resources));
    }

    private static string TempDirectory(string prefix) =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"))).FullName;

    private static void Cleanup(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
