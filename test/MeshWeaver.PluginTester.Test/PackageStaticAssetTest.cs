using System.Reactive;
using MeshWeaver.Data;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// A PACKAGE's static web assets ride a container-built module bundle — the producer obligation
/// <c>Doc/Architecture/ModuleStaticAssets</c> calls the fourth one.
///
/// <para>The Radzen view pack (MeshWeaver.Plugins, <c>build: container</c>) self-loads
/// <c>_content/Radzen.Blazor/Radzen.Blazor.js</c> and <c>_content/Radzen.Blazor/css/material-base.css</c>.
/// <c>dotnet publish</c> lays those at <c>wwwroot/_content/Radzen.Blazor/</c>; the in-image builder
/// runs no NuGet and copied only the project's own <c>wwwroot</c>, so the bundle carried one file, the
/// portal answered 404 for both, and every chart drew empty. These tests run the real
/// <see cref="ProjectBuild"/> against a real shelf directory and read the emitted tree.</para>
///
/// <para>🚨 Each positive has a negative control: a shelf package that ships NO assets contributes
/// none, and a package the landing image supplies (its host serves the assets) contributes none —
/// otherwise a builder that copied the whole shelf's <c>wwwroot</c> into every module would pass.</para>
/// </summary>
public class PackageStaticAssetTest : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mw-pkg-assets-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>An <c>/app</c>-shaped fixture: the shipped deps.json plus the process's TPA.</summary>
    private string AppDirectory()
    {
        var app = Path.Combine(_root, "_container");
        if (Directory.Exists(app))
            return app;
        Directory.CreateDirectory(app);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "mw-plugin-test.deps.json"),
            Path.Combine(app, "mw-plugin-test.deps.json"));
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "MeshWeaver.ShortGuid.dll"),
            Path.Combine(app, "MeshWeaver.ShortGuid.dll"));
        return app;
    }

    /// <summary>A real assembly on the shelf, so the module can compile against the package.</summary>
    private static void EmitLibrary(string directory, string assemblyName)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText($"namespace {assemblyName} {{ public static class Marker {{ public const int Value = 1; }} }}")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var emit = compilation.Emit(Path.Combine(directory, assemblyName + ".dll"));
        Assert.True(emit.Success, string.Join("; ", emit.Diagnostics));
    }

    /// <summary>
    /// The shape a Razor-SDK publish of the shelf project produces (measured with SDK 11.0.100 and
    /// Radzen.Blazor 13.0.0: 39 files under <c>wwwroot/_content/Radzen.Blazor/</c>, plus
    /// <c>&lt;Shelf&gt;.staticwebassets.endpoints.json</c>): <c>Acme.Charts</c> ships assets,
    /// <c>Acme.Plain</c> ships none.
    /// </summary>
    private string Shelf(bool withAssets = true)
    {
        var shelf = Path.Combine(_root, "_shelf");
        Directory.CreateDirectory(shelf);
        EmitLibrary(shelf, "Acme.Charts");
        EmitLibrary(shelf, "Acme.Plain");
        File.WriteAllText(Path.Combine(shelf, "Shelf.deps.json"), """
            {
              "targets": {
                "net11.0": {
                  "Shelf/1.0.0": { "dependencies": { "Acme.Charts": "13.0.0", "Acme.Plain": "2.0.0" } },
                  "Acme.Charts/13.0.0": { "runtime": { "lib/net11.0/Acme.Charts.dll": {} } },
                  "Acme.Plain/2.0.0": { "runtime": { "lib/net11.0/Acme.Plain.dll": {} } }
                }
              }
            }
            """);
        if (withAssets)
        {
            Write("_shelf/Shelf.staticwebassets.endpoints.json", "{}");
            Write("_shelf/wwwroot/_content/Acme.Charts/Acme.Charts.js", "export const charts = 1;");
            Write("_shelf/wwwroot/_content/Acme.Charts/css/material-base.css", ".rz { }");
        }
        return shelf;
    }

    private static string Project(string assemblyName, string package) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net11.0</TargetFramework>
            <AssemblyName>{assemblyName}</AssemblyName>
            <RootNamespace>Pack</RootNamespace>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="{package}" Version="1.0.0" />
          </ItemGroup>
        </Project>
        """;

    private Task<ProjectBuild.Report> Build(string project, string shelf) =>
        ProjectBuild.Run(new ProjectBuild.Options
        {
            EntryProjects = [project],
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "out"),
            Output = TextWriter.Null,
            MaxParallel = 1,
            ModuleLibrariesDirectory = shelf,
        }).Await(TestContext.Current.CancellationToken);

    private string Out(string assemblyName, string relative) =>
        Path.Combine(_root, "out", assemblyName, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task APackagesStaticWebAssets_rideTheModule_atWwwrootContentPackageId()
    {
        var shelf = Shelf();
        var project = Write("Pack/Pack.csproj", Project("Pack.Charts", "Acme.Charts"));
        Write("Pack/View.cs", "namespace Pack; public static class View { public static int V => Acme.Charts.Marker.Value; }");
        Write("Pack/wwwroot/css/own.css", ".own { }");

        var report = await Build(project, shelf);

        report.FatalError.Should().BeNull();
        report.ExitCode.Should().Be(0);
        File.Exists(Out("Pack.Charts", "wwwroot/_content/Acme.Charts/Acme.Charts.js")).Should().BeTrue(
            "the package's script is what the view self-loads at _content/Acme.Charts/Acme.Charts.js — "
            + "dotnet publish lays it at wwwroot/_content/<PackageId>/ and so must this builder");
        File.ReadAllText(Out("Pack.Charts", "wwwroot/_content/Acme.Charts/css/material-base.css"))
            .Should().Be(".rz { }", "the asset is copied byte-for-byte at its package-relative path");
        File.Exists(Out("Pack.Charts", "wwwroot/css/own.css")).Should().BeTrue(
            "the project's own wwwroot still lands beside the package's assets");
    }

    [Fact]
    public async Task NegativeControl_aShelfPackageWithNoAssets_contributesNone()
    {
        var shelf = Shelf();
        var project = Write("Plain/Plain.csproj", Project("Pack.Plain", "Acme.Plain"));
        Write("Plain/View.cs", "namespace Pack; public static class View { public static int V => Acme.Plain.Marker.Value; }");

        var report = await Build(project, shelf);

        report.ExitCode.Should().Be(0);
        Directory.Exists(Out("Pack.Plain", "wwwroot/_content")).Should().BeFalse(
            "only the packages of THIS module's closure ride — never the whole shelf's wwwroot");
    }

    [Fact]
    public void NegativeControl_aPackageTheLandingImageSupplies_ridesNoAssets()
    {
        var shelf = ModuleLibrariesShelf.Read(Shelf());

        shelf.HasStaticWebAssetPipeline.Should().BeTrue();
        shelf.StaticWebAssetsOf("Acme.Charts").Should().Equal(["Acme.Charts.js", "css/material-base.css"]);
        shelf.Resolve("Acme.Charts", _ => false)!.StaticAssetPackages.Should().Equal(["Acme.Charts"]);
        shelf.Resolve("Acme.Charts", n => n == "Acme.Charts")!.StaticAssetPackages.Should().BeEmpty(
            "the host serves the assets of a package its image supplies, and the host lane never lets a "
            + "module shadow them");
    }

    [Fact]
    public void AShelfPublishedWithoutTheStaticWebAssetPipeline_saysSo()
    {
        var shelf = ModuleLibrariesShelf.Read(Shelf(withAssets: false));

        shelf.HasStaticWebAssetPipeline.Should().BeFalse(
            "a plain Microsoft.NET.Sdk shelf publish copies no package assets, and the builder must be "
            + "able to NAME that rather than read it as 'the package ships none'");
        shelf.StaticWebAssetsOf("Acme.Charts").Should().BeEmpty();
    }
}
