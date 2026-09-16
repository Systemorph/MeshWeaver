using System;
using System.IO;
using System.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.PluginTester;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

public sealed class PublishedHostSurfaceTest : IDisposable
{
    private readonly string app = Directory.CreateTempSubdirectory("mw-published-host-").FullName;

    [Fact]
    public void IncludesThePortalAndItsSeededModules_NotTheTesterOrPrivateSiblings()
    {
        var portal = Emit(app, "MeshWeaver.PortalOnly", "PortalType");
        var moduleDirectory = Path.Combine(app, "modules", "MeshWeaver.Seeded");
        var seeded = Emit(moduleDirectory, "MeshWeaver.Seeded", "SeededType");
        Emit(moduleDirectory, "MeshWeaver.PrivateSibling", "PrivateType");
        var consumer = Emit(Path.Combine(app, "landed"), "MeshWeaver.Consumer", "Consumer : SeededType",
            MetadataReference.CreateFromFile(seeded));

        // The old publication had only the top-level reference set, just like the tester.
        var oldSurface = ModulePlatformSurface.OfFiles([portal]);
        Assert.False(oldSurface.Carries("MeshWeaver.Seeded"));
        Assert.Equal(ModuleLinkState.Unlinkable, ModulePlatformLink.Check(consumer, oldSurface).State);
        var surface = ModulePlatformSurface.FromJson(
            PublishedHostSurface.Read(app, [portal]).ToJson("s-target"));

        Assert.Equal("s-target", surface.Identity);
        Assert.Contains("PortalType", surface.TypesOf("MeshWeaver.PortalOnly")!);
        Assert.Contains("SeededType", surface.TypesOf("MeshWeaver.Seeded")!);
        Assert.False(surface.Carries("MeshWeaver.PrivateSibling"));
        Assert.False(surface.Carries("mw-plugin-test"));
        var verdict = ModulePlatformLink.Check(consumer, surface);
        Assert.Equal(ModuleLinkState.Linkable, verdict.State);
        Assert.True(verdict.CheckedTypeReferences > 0, verdict.Report());
    }

    [Fact]
    public void TheSeededEntryWinsOverTheAppCopy_ExactlyLikeTheRuntimeResolver()
    {
        var root = Emit(app, "MeshWeaver.Shared", "CurrentType");
        var seed = Emit(Path.Combine(app, "modules", "MeshWeaver.Shared"), "MeshWeaver.Shared", "SeedType");
        Assert.Equal(seed, MeshBuilder.ResolveModulePath("MeshWeaver.Shared.dll", app));
        var surface = PublishedHostSurface.Read(app, [root]);
        Assert.Contains("SeedType", surface.TypesOf("MeshWeaver.Shared")!);
        Assert.DoesNotContain("CurrentType", surface.TypesOf("MeshWeaver.Shared")!);
    }

    [Fact]
    public void UnreadableSeededAssemblyIsAnError_NotAnOmission()
    {
        var root = Emit(app, "MeshWeaver.Broken", "PortalType");
        var directory = Path.Combine(app, "modules", "MeshWeaver.Broken");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "MeshWeaver.Broken.dll"), "not an assembly");
        var error = Assert.Throws<InvalidDataException>(() => PublishedHostSurface.Read(app, [root]));
        Assert.Contains("MeshWeaver.Broken", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyHostCannotPublishAReadableSurface()
        => Assert.Throws<InvalidDataException>(() => PublishedHostSurface.Read(app, []));

    private static string Emit(string directory, string name, string type, params MetadataReference[] references)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, name + ".dll");
        var compilation = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText($"public class {type} {{ }}")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), .. references],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(file);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.ToString())));
        return file;
    }

    public void Dispose() => Directory.Delete(app, recursive: true);
}
