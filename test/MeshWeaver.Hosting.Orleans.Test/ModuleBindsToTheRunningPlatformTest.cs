using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MeshWeaver.Compiler;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>The binder half of the ladder</b> — policy <c>platform-backwards-compatibility</c>
/// (Doc/Architecture/PlatformCompatibilityLadder): "don't hard-link; the linker must accept a lower
/// version; the floor says what is the lowest possible". A plugin compiled against an OLDER build of
/// a platform assembly (a lower <c>AssemblyVersion</c> — the shape a minor bump of the platform
/// produces for every plugin built before it) must bind to the RUNNING platform's copy, never to a
/// private copy riding in its own folder, and serve.
///
/// <para><b>Real bytes, the real load context.</b> A stand-in <c>MeshWeaver.Mesh.Contract</c> is
/// compiled with Roslyn at a chosen version carrying just the member the plugin calls; the plugin is
/// compiled against it; BOTH are written into the module folder — exactly the "a bundle carries its
/// own copy" shape (#3662) — and the plugin is loaded through <see cref="ModulesAssemblyLoadContext"/>,
/// the context the platform loads module assemblies in. Then the plugin is handed a REAL
/// <see cref="MeshNode"/> from this process: that call succeeds only when the plugin's
/// <c>MeshNode</c> IS the running platform's type.</para>
///
/// <para><b>The negative control</b> is the rule's other edge: a plugin compiled against a HIGHER
/// version than the running platform cannot bind to it (.NET binds forward, never back), the private
/// copy wins, the identity splits — and the link probe declines it loudly
/// (<see cref="ModuleLinkState.BindingConflict"/>) before anything loads it.</para>
/// </summary>
public class ModuleBindsToTheRunningPlatformTest : IDisposable
{
    private const string Contract = "MeshWeaver.Mesh.Contract";

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-bind-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the per-test root.</summary>
    public ModuleBindsToTheRunningPlatformTest() => Directory.CreateDirectory(root);

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 🚨 THE RULE: compiled against a LOWER platform version than the running one ⇒ binds to the
    /// running assembly (not the private copy beside it) and serves.
    /// </summary>
    [Fact]
    public void APluginBuiltAgainstALowerPlatformVersion_BindsToTheRunningPlatform_AndServes()
    {
        var running = typeof(MeshNode).Assembly;
        var r = running.GetName().Version!;
        var lower = r.Minor > 0 ? new Version(r.Major, r.Minor - 1, 0, 0) : new Version(r.Major - 1, 9, 0, 0);
        var folder = ModuleFolder("MeshWeaver.Test.LowerBoundPlugin", lower);

        var context = new ModulesAssemblyLoadContext(folder);
        try
        {
            var plugin = context.LoadFromAssemblyPath(Path.Combine(folder, "MeshWeaver.Test.LowerBoundPlugin.dll"));
            var probe = plugin.GetType("Probe", throwOnError: true)!;

            var bound = (Assembly)probe.GetMethod("Bound")!.Invoke(null, null)!;
            Assert.Same(running, bound);
            Assert.NotEqual(folder, Path.GetDirectoryName(bound.Location));

            var served = (string)probe.GetMethod("IdOf")!.Invoke(null, [new MeshNode("ladder-node")])!;
            Assert.Equal("ladder-node", served);
        }
        finally
        {
            context.Unload();
        }

        // And the link probe agrees: the lower reference is forward drift, reported, never refused.
        var verdict = ModulePlatformLink.Check(
            Path.Combine(folder, "MeshWeaver.Test.LowerBoundPlugin.dll"),
            ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory));
        Assert.True(verdict.MayLoad, verdict.Report());
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL — compiled against a HIGHER platform version than the running one (its
    /// floor is above this platform): the loader cannot bind it to the running copy, the private copy
    /// wins and the type identity SPLITS (a real <see cref="MeshNode"/> cannot be handed to it) — which
    /// is exactly why the link probe declines it loudly as a binding conflict before it loads.
    /// </summary>
    [Fact]
    public void APluginBuiltAgainstAHigherPlatformVersion_CannotBindToTheRunningPlatform_AndIsDeclinedLoudly()
    {
        var running = typeof(MeshNode).Assembly;
        var higher = new Version(running.GetName().Version!.Major, running.GetName().Version!.Minor + 1, 0, 0);
        var folder = ModuleFolder("MeshWeaver.Test.HigherBoundPlugin", higher);

        var verdict = ModulePlatformLink.Check(
            Path.Combine(folder, "MeshWeaver.Test.HigherBoundPlugin.dll"),
            ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory));
        Assert.Equal(ModuleLinkState.BindingConflict, verdict.State);
        Assert.Contains(Contract, verdict.Report(), StringComparison.Ordinal);
        Assert.Contains(higher.ToString(4), verdict.Report(), StringComparison.Ordinal);

        var context = new ModulesAssemblyLoadContext(folder);
        try
        {
            var plugin = context.LoadFromAssemblyPath(Path.Combine(folder, "MeshWeaver.Test.HigherBoundPlugin.dll"));
            var probe = plugin.GetType("Probe", throwOnError: true)!;
            var bound = (Assembly)probe.GetMethod("Bound")!.Invoke(null, null)!;
            Assert.NotSame(running, bound);
            Assert.ThrowsAny<ArgumentException>(() => probe.GetMethod("IdOf")!.Invoke(null, [new MeshNode("x")]));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>Compiles a stand-in platform contract at <paramref name="contractVersion"/> and a
    /// plugin against it, and writes BOTH into one module folder.</summary>
    private string ModuleFolder(string pluginName, Version contractVersion)
    {
        var references = CompileReferences.Default
            .Where(r => r.Display is { } p && p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(Path.GetFileNameWithoutExtension(p), Contract, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var contract = Emit(Contract, $$"""
            [assembly: System.Reflection.AssemblyVersion("{{contractVersion.ToString(4)}}")]
            namespace MeshWeaver.Mesh;
            /// <summary>Stand-in: the member the plugin calls, nothing else.</summary>
            public record MeshNode(string Id, string? Namespace = null);
            """, references);
        var plugin = Emit(pluginName, """
            using MeshWeaver.Mesh;
            public static class Probe
            {
                public static string IdOf(MeshNode node) => node.Id;
                public static System.Reflection.Assembly Bound() => typeof(MeshNode).Assembly;
            }
            """, [.. references, MetadataReference.CreateFromImage(contract)]);

        var folder = Path.Combine(root, pluginName);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, Contract + ".dll"), contract);
        File.WriteAllBytes(Path.Combine(folder, pluginName + ".dll"), plugin);
        return folder;
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
