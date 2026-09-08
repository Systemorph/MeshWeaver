using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Connection.Orleans;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>The module load context resolves through the PLATFORM before its own directory (#3662,
/// half B).</b>
///
/// <para><b>What went wrong.</b> <see cref="ModulesAssemblyLoadContext.Load"/> used to prefer a
/// copy ALREADY LOADED in the default context, then the working directory, then the module
/// directory. A platform assembly the process had not touched yet — and the portal image carries
/// 214 of them, most of them cold at any moment — matched nothing in the first step, so a bundle
/// copy of it in the module directory won the module's context; when the platform later loaded
/// its own copy, the process held two assemblies of one identity, and every type that crossed the
/// boundary failed with an <c>InvalidCastException</c> between two types of the same full name.
/// That is the hazard MeshWeaver.Plugins' <c>platform-shipped.txt</c> exists to prevent at pack
/// time; this is the same rule enforced at load time.</para>
///
/// <para><b>The rule.</b> Ask the default context to RESOLVE the name first — a platform assembly
/// is served by the platform whether or not it is loaded yet — and fall through to the working
/// directory and the module directory only when the platform cannot supply the name at all.
/// Deterministic unit tests over the real context: no cluster, no mocks.</para>
/// </summary>
public class ModulesAssemblyLoadContextTest : IDisposable
{
    private readonly string moduleDirectory =
        Path.Combine(Path.GetTempPath(), "mw-module-alc-" + Guid.NewGuid().ToString("N"));

    private readonly ModulesAssemblyLoadContext context;

    public ModulesAssemblyLoadContextTest()
    {
        Directory.CreateDirectory(moduleDirectory);
        context = new ModulesAssemblyLoadContext(moduleDirectory);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        context.Unload();
        try { Directory.Delete(moduleDirectory, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 🚨 THE repro. A platform assembly this process has NOT loaded yet, with a same-named decoy
    /// sitting in the module directory: the platform's copy is served, from the default context,
    /// and the decoy is never touched. Before the fix the first step found nothing loaded, the
    /// second found nothing in the working directory, and the third loaded the decoy.
    /// </summary>
    [Fact]
    public void ANameThePlatformHas_IsServedFromTheDefaultContext_EvenWithACopyInTheModuleDirectory()
    {
        var (name, platformPath) = PlatformAssemblyNotYetLoaded();
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies,
            a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        File.WriteAllBytes(Path.Combine(moduleDirectory, name + ".dll"),
            Emit(name, "public static class Decoy { }"));

        var loaded = context.LoadFromAssemblyName(new AssemblyName(name));

        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(loaded));
        Assert.Equal(name, loaded.GetName().Name);
        Assert.False(loaded.Location.StartsWith(moduleDirectory, StringComparison.OrdinalIgnoreCase),
            $"the module directory's decoy was loaded instead of the platform's {platformPath}");
        Assert.Null(loaded.GetType("Decoy", throwOnError: false));
    }

    /// <summary>
    /// The positive control for the fall-through: a name the platform LACKS is served from the
    /// module directory, into the module's own collectible context — which is what the context is
    /// for. Removing the fall-through is what this catches.
    /// </summary>
    [Fact]
    public void ANameThePlatformLacks_IsServedFromTheModuleDirectory()
    {
        var name = "MeshWeaver.Test.ModuleOnly" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(Path.Combine(moduleDirectory, name + ".dll"),
            Emit(name, "public static class Only { }"));

        var loaded = context.LoadFromAssemblyName(new AssemblyName(name));

        Assert.Same(context, AssemblyLoadContext.GetLoadContext(loaded));
        Assert.StartsWith(moduleDirectory, loaded.Location, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(loaded.GetType("Only", throwOnError: false));
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies,
            a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A name nobody has — neither the platform, the working directory nor the module
    /// directory — still resolves to null rather than throwing out of <c>Load</c>.</summary>
    [Fact]
    public void ANameNobodyHas_IsNotFound_NotAThrowFromLoad()
    {
        var name = "MeshWeaver.Test.Nowhere" + Guid.NewGuid().ToString("N")[..8];
        Assert.Throws<FileNotFoundException>(() => context.LoadFromAssemblyName(new AssemblyName(name)));
    }

    // ───────────────────────────────────────────────────────────── harness

    /// <summary>
    /// A platform (TPA) assembly this process has not loaded — measured, not assumed: the test
    /// asserts the precondition, because an already-loaded name is served by the OLD order too
    /// and would prove nothing.
    /// </summary>
    private static (string Name, string Path) PlatformAssemblyNotYetLoaded()
    {
        var loaded = AssemblyLoadContext.Default.Assemblies
            .Select(a => a.GetName().Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Select(p => (Name: Path.GetFileNameWithoutExtension(p), Path: p))
            .Where(p => p.Name.StartsWith("System.", StringComparison.Ordinal) && !loaded.Contains(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        Assert.False(candidate == default, "every platform assembly is already loaded — nothing to measure with");
        return candidate;
    }

    /// <summary>Compiles one source file into an assembly named <paramref name="assemblyName"/>,
    /// against this process's own reference set.</summary>
    private static byte[] Emit(string assemblyName, string source)
    {
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            // Never reference the assembly being impersonated: the decoy must not depend on
            // the platform copy of itself.
            .Where(p => !string.Equals(Path.GetFileNameWithoutExtension(p), assemblyName,
                StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

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
