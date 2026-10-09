using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.PluginTester;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// 🚨 <c>platform-link</c> measures each module against the platform PLUS the set's sibling modules
/// — the surface the landing measures it against (the application closure plus every landed
/// module). A module that references another MODULE (the AI module → Markdown.Collaboration, which
/// ships as the Essentials bundle, not in the image) was refused "no such platform assembly" when
/// the image stopped carrying the sibling (MeshWeaver.Plugins#2970), and that false red froze CD.
/// </summary>
public sealed class PlatformLinkSiblingModulesTest : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("mw-platform-link-siblings-").FullName;

    [Fact]
    public void AModuleReferencingASiblingModuleOfTheSet_Links()
    {
        var (sibling, dependent) = Bundles();
        var platform = new[] { typeof(object).Assembly.Location };

        var results = PlatformLink.CheckBundles([sibling, dependent], platform, ModuleLinkOptions.WithMembers, Work());

        var verdict = results.Single(r => r.Verdict.Module == "MeshWeaver.Dependent").Verdict;
        Assert.True(verdict.MayLoad, verdict.Report());
        Assert.True(verdict.CheckedTypeReferences > 0, verdict.Report());
        Assert.True(verdict.CheckedMemberReferences > 0, verdict.Report());
    }

    /// <summary>NEGATIVE CONTROL: the same dependent bundle WITHOUT its sibling in the set is still red —
    /// the sibling rule widens the surface by the set's own modules, never by "anything named MeshWeaver.*".</summary>
    [Fact]
    public void WithoutTheSiblingInTheSet_TheSameModuleIsRed()
    {
        var (_, dependent) = Bundles();
        var platform = new[] { typeof(object).Assembly.Location };

        var results = PlatformLink.CheckBundles([dependent], platform, ModuleLinkOptions.WithMembers, Work());

        var verdict = results.Single(r => r.Verdict.Module == "MeshWeaver.Dependent").Verdict;
        Assert.False(verdict.MayLoad, verdict.Report());
        Assert.Contains("MeshWeaver.Sibling", verdict.Report(), StringComparison.Ordinal);
    }

    private (string Sibling, string Dependent) Bundles()
    {
        var build = Path.Combine(root, "build-" + Guid.NewGuid().ToString("N")[..8]);
        var sibling = Emit(Path.Combine(build, "sibling"), "MeshWeaver.Sibling", "public class SiblingType { }");
        var dependent = Emit(Path.Combine(build, "dependent"), "MeshWeaver.Dependent",
            "public class DependentType : SiblingType { }", MetadataReference.CreateFromFile(sibling));
        return (Pack(build, "essentials", sibling), Pack(build, "ai", dependent));
    }

    private static string Pack(string directory, string name, string dll)
    {
        var bundle = Path.Combine(directory, name + ".module.nupkg");
        using var archive = ZipFile.Open(bundle, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(dll, "meshweaver/modules/" + Path.GetFileName(dll));
        return bundle;
    }

    private string Work() => Directory.CreateDirectory(Path.Combine(root, "work-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private static string Emit(string directory, string name, string source, params MetadataReference[] references)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, name + ".dll");
        var compilation = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), .. references],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(file);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.ToString())));
        return file;
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* temp cleanup is the OS's problem */ }
    }
}
