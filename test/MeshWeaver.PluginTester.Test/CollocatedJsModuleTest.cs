using System.Reactive;
using MeshWeaver.Data;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The Razor SDK's THIRD static-asset kind in <c>build-project</c>: a collocated JS module —
/// <c>Foo.razor.js</c> beside <c>Foo.razor</c> — must reach <c>wwwroot/&lt;project-relative&gt;</c>
/// in the build output, because that is the only place the packer looks and the only shape the
/// portal serves at <c>_content/&lt;Name&gt;/…</c>.
///
/// <para>🚨 Why this needs a test of its own (Systemorph/MeshWeaver#2384): nothing else fails when
/// the file is dropped. The project compiles, the bundle packs, the manifest declares a non-empty
/// <c>staticAssets</c> (the pack's <em>other</em> assets are there), the module lands, it loads,
/// and the component renders — until <c>OnAfterRenderAsync</c> asks the browser to
/// <c>import('./_content/&lt;Name&gt;/Foo.razor.js')</c> and gets a 404. Measured live on
/// memex.meshweaver.cloud 2026-09-15: <c>leaflet.css</c> (14,806 B) and <c>leaflet-src.esm.js</c>
/// (424,589 B) served 200 out of the very same landed module folder whose
/// <c>OpenStreetMapView.razor.js</c> answered 404 — and so did GoogleMaps', AppleMaps' and Chat's.
/// Every map in every container-built view pack had been blank for three weeks.</para>
///
/// <para>The fixtures are deliberately the two production shapes: a pack that HAS a
/// <c>wwwroot/</c> (OpenStreetMap's — 7 vendored Leaflet files, which is exactly why a
/// count-based gate read healthy) and a pack that has NONE at all (AppleMaps' and GoogleMaps'
/// — their whole asset surface is the collocated JS).</para>
/// </summary>
public class CollocatedJsModuleTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mw-jsmodule-{Guid.NewGuid():N}");

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>The image's /app stand-in, exactly as <see cref="ScopedCssTest"/> composes it.</summary>
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

    private static string Csproj(string assemblyName) => $"""
        <Project Sdk="Microsoft.NET.Sdk.Razor">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <AssemblyName>{assemblyName}</AssemblyName>
            <RootNamespace>Widgets</RootNamespace>
          </PropertyGroup>
        </Project>
        """;

    private const string Component = """
        <div class="badge">@Text</div>
        @code { [Microsoft.AspNetCore.Components.Parameter] public string? Text { get; set; } }
        """;

    private Task<ProjectBuild.Report> Build(string entry) =>
        ProjectBuild.Run(new()
        {
            EntryProjects = [entry],
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "out"),
            Output = TextWriter.Null,
            MaxParallel = 2,
        }).Await(TestContext.Current.CancellationToken);

    /// <summary>
    /// The pack that HAS a <c>wwwroot/</c> — OpenStreetMap's shape. Its vendored files ride (they
    /// always did), AND the collocated JS module lands beside them at the path the SDK publishes it
    /// at, byte-identical.
    /// </summary>
    [Fact]
    public async Task TheCollocatedJsModuleLandsAtItsProjectRelativePath()
    {
        const string js = "export function initMap(el){ return el; }\n";
        var entry = Write("Osm/Osm.csproj", Csproj("Widgets.Osm"));
        Write("Osm/Components/MapView.razor", Component);
        Write("Osm/Components/MapView.razor.js", js);
        Write("Osm/wwwroot/leaflet/leaflet.css", ".leaflet{}\n");

        var report = await Build(entry);

        report.FatalError.Should().BeNull();
        report.ExitCode.Should().Be(0);
        var outputDirectory = Path.GetDirectoryName(report.Projects.Single().Result!.AssemblyPath!)!;

        // 🚨 THE ASSERTION #2384 IS ABOUT. The path is the contract: the packer sweeps
        // <out>/wwwroot into staticAssets, the landing writes it module-relative, and the host
        // mounts <module dir>/wwwroot at _content/<Name>, so THIS file is what the component's
        // `import('./_content/Widgets.Osm/Components/MapView.razor.js')` resolves to.
        var landed = Path.Combine(outputDirectory, "wwwroot", "Components", "MapView.razor.js");
        File.Exists(landed).Should().BeTrue(
            "a collocated *.razor.js is a static web asset the SDK COMPUTES — it is not under the "
            + "project's wwwroot/, so a builder that only copies wwwroot/ drops it and the "
            + "component's dynamic import 404s at first render (#2384)");
        File.ReadAllText(landed).Should().Be(js, "the bytes the browser imports are the authored ones");

        // The pre-existing kind must not be collateral: the pack's own wwwroot still rides.
        File.Exists(Path.Combine(outputDirectory, "wwwroot", "leaflet", "leaflet.css"))
            .Should().BeTrue("the verbatim wwwroot copy is what already worked; it must keep working");
    }

    /// <summary>
    /// The pack with NO <c>wwwroot/</c> and no <c>*.razor.css</c> — AppleMaps' shape, whose whole
    /// asset surface is one collocated JS module. It is the case every count-based check skipped.
    /// </summary>
    [Fact]
    public async Task APackWhoseONLYAssetIsItsJsModuleStillEmitsAWwwroot()
    {
        var entry = Write("Apple/Apple.csproj", Csproj("Widgets.Apple"));
        Write("Apple/AppleMapView.razor", Component);
        Write("Apple/AppleMapView.razor.js", "export function init(){}\n");

        var report = await Build(entry);

        report.ExitCode.Should().Be(0);
        var outputDirectory = Path.GetDirectoryName(report.Projects.Single().Result!.AssemblyPath!)!;
        File.Exists(Path.Combine(outputDirectory, "wwwroot", "AppleMapView.razor.js"))
            .Should().BeTrue(
                "a project with no wwwroot/ and no scoped CSS still ships one asset, and it is the "
                + "one its view cannot render without");
    }

    /// <summary>
    /// The OTHER pairing the SDK recognises: a <c>Foo.cshtml.js</c> beside a Razor Pages/MVC
    /// <c>Foo.cshtml</c>. It travels a different branch of the emitter (the Razor item is not a
    /// component) to the same project-relative destination, so a regression in it would 404
    /// exactly like #2384 while a `.razor.js`-only suite stayed green.
    /// </summary>
    [Fact]
    public async Task ACshtmlViewsCollocatedJsModuleLandsTheSameWay()
    {
        const string js = "export function page(){ return 'p'; }\n";
        var entry = Write("Pages/Pages.csproj", Csproj("Widgets.Pages"));
        Write("Pages/Views/Index.cshtml", "@{ }\n<p>index</p>\n");
        Write("Pages/Views/Index.cshtml.js", js);

        var report = await Build(entry);

        report.ExitCode.Should().Be(0,
            $"the fixture must build for this case to measure anything — {report.Projects.Single().Result?.Failure}");
        var outputDirectory = Path.GetDirectoryName(report.Projects.Single().Result!.AssemblyPath!)!;
        var landed = Path.Combine(outputDirectory, "wwwroot", "Views", "Index.cshtml.js");
        File.Exists(landed).Should().BeTrue(
            "a .cshtml view's collocated JS is the same SDK asset kind as a component's, and it is "
            + "requested at the same _content/<Name>/<project-relative path>");
        File.ReadAllText(landed).Should().Be(js);
    }

    /// <summary>
    /// The same refusal on the <c>.cshtml</c> side — proving the orphan check keys off the PAIRING,
    /// not off the <c>.razor</c> extension.
    /// </summary>
    [Fact]
    public async Task ACshtmlJsModuleThatPairsWithNothingIsRefusedToo()
    {
        var entry = Write("OrphanPage/OrphanPage.csproj", Csproj("Widgets.OrphanPage"));
        Write("OrphanPage/Views/Index.cshtml", "@{ }\n<p>index</p>\n");
        Write("OrphanPage/Views/Index.cshtml.js", "export function ok(){}\n");
        Write("OrphanPage/Views/Removed.cshtml.js", "export function stranded(){}\n");

        var report = await Build(entry);

        report.ExitCode.Should().NotBe(0);
        report.Projects.Single().Result!.Failure.Should()
            .Contain("Removed.cshtml.js").And.Contain("BLAZOR106");
    }

    /// <summary>
    /// NEGATIVE CONTROL for the emitter itself: a JS module that pairs with nothing is REFUSED by
    /// name — the SDK's own BLAZOR106, which is an error there too (measured against SDK 10.0.400,
    /// 2026-09-15, including for a file under <c>wwwroot/</c>). Without this a component renamed
    /// away from its JS reproduces #2384 from the other direction, silently.
    /// </summary>
    [Fact]
    public async Task AJsModuleThatPairsWithNothingIsRefusedByName()
    {
        var entry = Write("Orphan/Orphan.csproj", Csproj("Widgets.Orphan"));
        Write("Orphan/Components/MapView.razor", Component);
        Write("Orphan/Components/MapView.razor.js", "export function ok(){}\n");
        // Renamed away from its component — the shape that reaches production.
        Write("Orphan/Components/MapViewOld.razor.js", "export function stranded(){}\n");

        var report = await Build(entry);

        report.ExitCode.Should().NotBe(0, "an asset that can never ship must not build green");
        report.Projects.Single().Result!.Failure.Should()
            .Contain("MapViewOld.razor.js").And.Contain("BLAZOR106");
    }

    /// <summary>
    /// CONTROL: a Razor project with no JS module at all builds green and grows no
    /// <c>wwwroot/</c>. Without it the two assertions above could be satisfied by an emitter that
    /// writes something for every project.
    /// </summary>
    [Fact]
    public async Task AProjectWithNoJsModuleEmitsNoWwwroot()
    {
        var entry = Write("Plain/Plain.csproj", Csproj("Widgets.Plain"));
        Write("Plain/Components/Badge.razor", Component);

        var report = await Build(entry);

        report.ExitCode.Should().Be(0);
        var outputDirectory = Path.GetDirectoryName(report.Projects.Single().Result!.AssemblyPath!)!;
        Directory.Exists(Path.Combine(outputDirectory, "wwwroot")).Should().BeFalse(
            "the asset set is driven by what the project HAS, never fabricated by the builder");
    }
}
