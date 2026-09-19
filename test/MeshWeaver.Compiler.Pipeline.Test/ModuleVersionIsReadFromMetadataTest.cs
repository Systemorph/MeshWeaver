using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b><see cref="InstalledModuleAssembly.VersionOf"/> reads ONE attribute and must not pay for
/// ALL of them</b> (MeshWeaver.Plugins#2116).
///
/// <para><c>Attribute.GetCustomAttributes(Assembly, Type)</c> resolves the declaring TYPE of every
/// assembly-level attribute record in order to test it against the filter. So an assembly carrying
/// an unrelated assembly-level attribute whose type lives in an assembly this process cannot bind
/// threw <see cref="FileNotFoundException"/> out of
/// <c>System.Reflection.CustomAttribute.FilterCustomAttributeRecord</c> — while the value being
/// asked for, an <see cref="AssemblyInformationalVersionAttribute"/> from corelib, sat in the
/// metadata untouched.</para>
///
/// <para><b>What that cost.</b> Measured 2026-09-18 on <c>mw-plugin-test compile … --module
/// &lt;dll&gt;</c>: <c>mw-plugin-test: FATAL — System.IO.FileNotFoundException …</c> and a
/// nine-frame reflection stack naming neither the module nor the missing assembly, exit 70. The
/// same read runs in the portal — <c>NodeTypeCompilationHelpers.ModuleVersionsOf</c> over every
/// installed module — so the crash was never the tester's alone.</para>
///
/// <para>🚨 <b>The two cases are BOTH here on purpose.</b> A suite that only proved "the awkward
/// assembly no longer throws" would pass over an implementation that returned null for everything;
/// a suite that only proved "an ordinary module still reports its stamp" would pass over the
/// unfixed code. One case on each side of the change — and the awkward case is built to be
/// awkward: <see cref="TheReflectionReadStillFails"/> asserts the OLD read still fails on this
/// fixture, so the fixture can never quietly stop reproducing the defect.</para>
/// </summary>
public class ModuleVersionIsReadFromMetadataTest : IDisposable
{
    private const string Stamp = "3.4.5-ci.8925+ab12cd34";
    private const string Ordered = "3.4.5-ci.8925";

    /// <summary>The assembly that DEFINES the marker attribute. It is emitted, referenced, and
    /// then deliberately left out of the module's directory, which is what makes the module's
    /// attribute closure incomplete — the shape a plain assembly mounted with <c>--module</c>
    /// has.</summary>
    private const string MarkerAssembly = "MeshWeaver.Test.AbsentMarker2116";

    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"mw-2116-{Guid.NewGuid():N}");

    /// <summary>A collectible context, so the fixtures do not pin the default ALC for the rest of
    /// the suite — and so a second test may load an assembly of the same simple name.</summary>
    private readonly AssemblyLoadContext context = new($"mw-2116-{Guid.NewGuid():N}", isCollectible: true);

    public void Dispose()
    {
        context.Unload();
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory costs disk, never correctness.
        }
    }

    [Fact]
    public void AnOrdinaryModulesStampIsUnchanged()
    {
        var assembly = LoadModule("MeshWeaver.Test.Ordinary2116", withAbsentMarker: false);

        // The value, and the +buildmetadata rule, exactly as before.
        Assert.Equal(Ordered, InstalledModuleAssembly.VersionOf(assembly));
        Assert.Equal(Ordered, new InstalledModuleAssembly(assembly).Version);
        // …and the reflection read agrees, which is what "unchanged" means.
        Assert.Equal(Stamp,
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }

    [Fact]
    public void AnUnstampedAssemblyStillReadsNull()
    {
        var assembly = LoadModule("MeshWeaver.Test.Unstamped2116", withAbsentMarker: false, stamp: null);

        // Null is not a version and never reads as one — the caller resolves the MVID instead.
        Assert.Null(InstalledModuleAssembly.VersionOf(assembly));
    }

    [Fact]
    public void AnIncompleteAttributeClosureDoesNotStopTheStampBeingRead()
    {
        var assembly = LoadModule("MeshWeaver.Test.Awkward2116", withAbsentMarker: true);

        // BEFORE the fix this line was the FATAL: FileNotFoundException for MarkerAssembly, out of
        // FilterCustomAttributeRecord, nine frames deep, naming no module.
        Assert.Equal(Ordered, InstalledModuleAssembly.VersionOf(assembly));
        Assert.Equal(Ordered, new InstalledModuleAssembly(assembly).Version);
    }

    /// <summary>
    /// 🚨 The fixture's own control. If this ever stops throwing, the awkward assembly has stopped
    /// being awkward — the marker assembly became resolvable, say — and
    /// <see cref="AnIncompleteAttributeClosureDoesNotStopTheStampBeingRead"/> would be passing
    /// over a defect it can no longer reach.
    /// </summary>
    [Fact]
    public void TheReflectionReadStillFails()
    {
        var assembly = LoadModule("MeshWeaver.Test.AwkwardControl2116", withAbsentMarker: true);

        var thrown = Assert.Throws<FileNotFoundException>(
            () => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>());
        Assert.Contains(MarkerAssembly, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Emits the fixture into its OWN directory and loads it from there, so its resolution surface
    /// is exactly its own closure. With <paramref name="withAbsentMarker"/> the marker assembly is
    /// referenced at compile time and NOT written beside it.
    /// </summary>
    private Assembly LoadModule(string name, bool withAbsentMarker, string? stamp = Stamp)
    {
        var marker = Emit(MarkerAssembly, """
            namespace MeshWeaver.Test;
            [System.AttributeUsage(System.AttributeTargets.Assembly)]
            public sealed class AbsentMarkerAttribute : System.Attribute;
            """);

        var stampLine = stamp is null
            ? string.Empty
            : $"""[assembly: System.Reflection.AssemblyInformationalVersion("{stamp}")]""";
        var markerLine = withAbsentMarker ? "[assembly: MeshWeaver.Test.AbsentMarker]" : string.Empty;
        var bytes = Emit(name, $"""
            {stampLine}
            {markerLine}
            namespace {name.Replace('.', '_')};
            public class Thing;
            """,
            withAbsentMarker ? MetadataReference.CreateFromImage(marker) : null);

        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".dll");
        File.WriteAllBytes(path, bytes);
        return context.LoadFromAssemblyPath(path);
    }

    private static byte[] Emit(string assemblyName, string source, MetadataReference? extra = null)
    {
        var references = PlatformReferences.Platform();
        if (extra is not null)
            references = references.Add(extra);

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return buffer.ToArray();
    }
}
