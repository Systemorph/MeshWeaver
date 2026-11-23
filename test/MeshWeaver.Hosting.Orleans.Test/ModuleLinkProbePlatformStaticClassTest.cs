using System;
using System.IO;
using System.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// The link probe must read the surface of the platform assembly a module will actually bind. A
/// module calling a public static class (extension host) the running platform DEFINES must link
/// (production false refusal of <c>MeshWeaver.Reactive.ParallelReadExtensions</c>, 4.0.13914), and a
/// type the platform truly lacks is still refused by name.
/// </summary>
public class ModuleLinkProbePlatformStaticClassTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-probe-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the per-test root.</summary>
    public ModuleLinkProbePlatformStaticClassTest() => Directory.CreateDirectory(root);

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>A module that calls an extension of a static class the running platform defines links.</summary>
    [Fact]
    public void ModuleCallingAPlatformStaticClass_Links()
    {
        var path = Module("StaticClassModule", """
            using System;
            using MeshWeaver.Reactive;
            public static class Probe
            {
                public static IObservable<int> Both(IObservable<int> a, IObservable<int> b) => a.WhenBoth(b, (x, y) => x + y);
            }
            """);
        var verdict = ModulePlatformLink.Check(path, ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory),
            ModuleLinkOptions.WithMembers);
        Assert.True(verdict.MayLoad, verdict.Report());
        Assert.Contains("MeshWeaver.Messaging.Hub", verdict.CheckedAssemblies);
    }

    /// <summary>
    /// 🚨 The production defect: the process also holds an OLDER same-named copy of a platform
    /// assembly in a secondary load context (a staged / superseded generation), and the process's
    /// assembly list happens to name it first. The module binds the DEFAULT context's copy
    /// (platform-first), so that is the copy the probe must read.
    /// </summary>
    [Fact]
    public void AStaleCopyInASecondaryContextListedFirst_DoesNotShadowThePlatformCopy()
    {
        var path = Module("ShadowedModule", """
            using System;
            using MeshWeaver.Reactive;
            public static class Probe
            {
                public static IObservable<int> Both(IObservable<int> a, IObservable<int> b) => a.WhenBoth(b, (x, y) => x + y);
            }
            """);
        var staleBytes = Emit("MeshWeaver.Messaging.Hub", """
            [assembly: System.Reflection.AssemblyVersion("4.0.0.0")]
            namespace MeshWeaver.Reactive { public static class AnOlderSurface { } }
            """, References(excluding: "MeshWeaver.Messaging.Hub"));
        var staleFolder = Path.Combine(root, "stale");
        Directory.CreateDirectory(staleFolder);
        var stalePath = Path.Combine(staleFolder, "MeshWeaver.Messaging.Hub.dll");
        File.WriteAllBytes(stalePath, staleBytes);
        var secondary = new System.Runtime.Loader.AssemblyLoadContext("stale-generation", isCollectible: true);
        try
        {
            var stale = secondary.LoadFromAssemblyPath(stalePath);
            var platform = typeof(MeshWeaver.Reactive.ParallelReadExtensions).Assembly;
            Assert.NotSame(stale, platform);

            var surface = ModulePlatformSurface.Of([stale, platform], AppContext.BaseDirectory);
            var verdict = ModulePlatformLink.Check(path, surface, ModuleLinkOptions.WithMembers);
            Assert.True(verdict.MayLoad, verdict.Report());
        }
        finally
        {
            secondary.Unload();
        }
    }

    /// <summary>Negative control: a type the platform truly lacks is refused, by name.</summary>
    [Fact]
    public void ModuleReferencingATypeThePlatformLacks_IsRefusedByName()
    {
        var hub = Emit("MeshWeaver.Messaging.Hub", """
            [assembly: System.Reflection.AssemblyVersion("4.0.0.0")]
            namespace MeshWeaver.Reactive { public static class NoSuchExtensionsAnywhere { public static int X() => 1; } }
            """, References(excluding: "MeshWeaver.Messaging.Hub"));
        var path = Module("MissingTypeModule", """
            public static class Probe { public static int Go() => MeshWeaver.Reactive.NoSuchExtensionsAnywhere.X(); }
            """, extra: MetadataReference.CreateFromImage(hub), excluding: "MeshWeaver.Messaging.Hub");
        var verdict = ModulePlatformLink.Check(path, ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory),
            ModuleLinkOptions.WithMembers);
        Assert.False(verdict.MayLoad);
        Assert.Contains("MeshWeaver.Reactive.NoSuchExtensionsAnywhere", verdict.Report(), StringComparison.Ordinal);
    }

    private static System.Collections.Generic.List<MetadataReference> References(string? excluding = null) =>
        [.. CompileReferences.Default.Where(r => r.Display is { } p && p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetFileNameWithoutExtension(p), excluding, StringComparison.OrdinalIgnoreCase))];

    private string Module(string name, string source, MetadataReference? extra = null, string? excluding = null)
    {
        var refs = References(excluding);
        if (extra is not null)
            refs.Add(extra);
        var bytes = Emit(name, source, refs);
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".dll");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Emit(string assemblyName, string source, System.Collections.Generic.IEnumerable<MetadataReference> references)
    {
        var compilation = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return buffer.ToArray();
    }
}
