using System.Reactive;
using System.Reflection;
using MeshWeaver.Data;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// <c>mw-plugin-test build-project</c> compiles Razor — the whole chain, executed: the generator the
/// image SHIPS is found beside the builder, loaded against THIS repo's Roslyn, and run over a real
/// <c>.razor</c> file whose emitted component type is then LOADED and its generated
/// <c>BuildRenderTree</c> override found on it.
///
/// <para>🚨 Every assertion here exists because the failure it guards is SILENT. A Razor generator
/// that does not load looks exactly like a project with broken components (a wall of CS0115); a
/// generator that loads but emits nothing looks the same; a <c>.razor</c> file the item glob never
/// found looks the same again. So the suite proves the positive (a component is generated and
/// loads) AND the three negatives (each failure names its own cause).</para>
///
/// <para>This is also the gate on the SDK pairing. The generator is built against the Roslyn of the
/// SDK it shipped in and the image carries the Roslyn this repo pins — the two disagreed by three
/// minor versions the day this was written (5.9.0.0 wanted, 5.6.0.0 present), and the load context
/// is what reconciles them. If a future SDK's Razor compiler cannot run against this repo's Roslyn,
/// these tests go red on the PR that bumps it rather than in an image nobody can debug.</para>
/// </summary>
public class RazorBuildTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mw-razorbuild-{Guid.NewGuid():N}");

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

    /// <summary>
    /// A Razor class library, exactly as every <c>MeshWeaver.Blazor.*</c> project declares itself.
    /// The assembly name is per-test because these tests LOAD what they emit into this process, and
    /// the default context refuses a second assembly of the same simple name; the root namespace
    /// stays fixed so the namespace assertions stay about the TargetPath.
    /// </summary>
    private static string RazorProject(string assemblyName) => $"""
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

    /// <summary>
    /// The same <c>/app</c>-shaped fixture <see cref="ProjectBuildTest"/> uses: the REAL shipped
    /// <c>deps.json</c> plus one assembly, with everything else arriving through the process's TPA
    /// and the shared frameworks — which is where <c>Microsoft.AspNetCore.Components</c> comes from,
    /// exactly as it does inside a portal image.
    /// </summary>
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

    private ProjectBuild.Options OptionsFor(string entry, params string[] accept) =>
        new()
        {
            EntryProjects = [entry],
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "out"),
            Output = TextWriter.Null,
            Accept = accept,
            MaxParallel = 2,
        };

    private static Task<ProjectBuild.Report> Build(ProjectBuild.Options options) =>
        ProjectBuild.Run(options).Await(TestContext.Current.CancellationToken);

    // ── what the image ships ───────────────────────────────────────────────────────────────────

    [Fact]
    public void TheImageShipsARazorGeneratorBesideTheBuilderAndItLOADS()
    {
        // Locate() with no override is the in-image path: razor-generators/ beside the builder.
        var directory = RazorGenerators.Locate(null, "/nonexistent-app");
        directory.Should().NotBeNull(
            "the image build stages the SDK's Razor source generator beside the builder; without it "
            + "no Blazor project can be compiled and every one reports CS0115 instead");

        // 🚨 The load is the assertion. The generator is a netstandard2.0 assembly built against a
        // DIFFERENT Roslyn than this process carries, so "the file is there" proves nothing.
        var set = RazorGenerators.Load(directory!, NullLogger.Instance);
        set.Generators.Should().NotBeEmpty();
        set.Generators.Select(g => g.GetGeneratorType().Name)
            .Should().Contain("RazorSourceGenerator");
        set.Provenance.Should().Contain("Microsoft.CodeAnalysis.Razor.Compiler.dll");
        set.Provenance.Should().Contain("\"sdk\"", "the provenance is the staged manifest, not a file listing");
    }

    [Fact]
    public void TheShippedClosureIsTheCompilerPlusItsOnePrivateDependency()
    {
        var directory = RazorGenerators.Locate(null, "/nonexistent-app")!;
        var assemblies = Directory.GetFiles(directory, "*.dll")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Measured, not assumed: everything else Razor.Compiler references (Microsoft.CodeAnalysis,
        // Microsoft.CodeAnalysis.CSharp, System.Collections.Immutable, System.Memory, System.Buffers,
        // netstandard) resolves to the HOST. Only Utilities.Shared has to travel with it — with that
        // file absent the compiler assembly still loads and every call into it throws.
        assemblies.Should().Contain("Microsoft.CodeAnalysis.Razor.Compiler.dll");
        assemblies.Should().Contain("Microsoft.AspNetCore.Razor.Utilities.Shared.dll");

        // 🚨 STAGED PER RID. The SDK crossgens its Razor compiler for the SDK's own runtime
        // identifier, so one copy cannot serve a multi-arch image — the directory it loads from is
        // named for this process's RID.
        Path.GetFileName(directory).Should().BeOneOf([.. RazorGenerators.RuntimeIdentifiers]);

        File.Exists(Path.Combine(
                Path.GetDirectoryName(directory)!, RazorGenerators.ManifestName))
            .Should().BeTrue(
                "the image must say WHICH Razor compiler it carries and for which architectures — "
                + "the image is the pin, and a pin nobody can read is not a pin");
    }

    // ── the positive: a real component compiles, and the type LOADS ────────────────────────────

    [Fact]
    public async Task ARealRazorComponentCompilesAndItsGeneratedOverrideIsONTheEmittedType()
    {
        var project = Write("Widgets/Widgets.csproj", RazorProject("Widgets.Spacer"));
        // The shape of MeshWeaver.Blazor.Views/Components/SpacerView.razor: an @inherits onto a base
        // in the same project, markup, and a @code block — the three things a component is made of.
        Write("Widgets/Base/WidgetBase.cs", """
            namespace Widgets.Base;
            /// <summary>A widget base, so @inherits has something real to point at.</summary>
            public abstract class WidgetBase : Microsoft.AspNetCore.Components.ComponentBase
            {
                /// <summary>The label every widget carries.</summary>
                protected string Label { get; set; } = "widget";
            }
            """);
        Write("Widgets/Spacer.razor", """
            @inherits Widgets.Base.WidgetBase

            <div class="spacer">@Label</div>

            @code {
                private int Height => 8;
            }
            """);

        var report = await Build(OptionsFor(project));

        report.FatalError.Should().BeNull();
        report.ExitCode.Should().Be(0);
        var result = report.Projects.Single().Result!;
        result.Failure.Should().BeNull();
        result.RazorCount.Should().BeGreaterThan(0, "the .razor file must have produced a document");
        result.AssemblyPath.Should().NotBeNull();

        // 🚨 LOAD it. "the compiler returned" is not evidence that a component exists — the whole
        // CS0115 family is about a type whose override half is missing, and only reflection over the
        // emitted assembly can tell the difference.
        var assembly = Assembly.LoadFrom(result.AssemblyPath!);
        var component = assembly.GetType("Widgets.Spacer");
        component.Should().NotBeNull("the component's namespace comes from RootNamespace + TargetPath");
        component!.BaseType!.FullName.Should().Be("Widgets.Base.WidgetBase");

        var buildRenderTree = component.GetMethod(
            "BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        buildRenderTree.Should().NotBeNull(
            "BuildRenderTree is what the Razor generator emits; a component without it is exactly the "
            + "CS0115 failure this whole feature exists to end");
    }

    [Fact]
    public async Task ASubdirectoryComponentLandsInTheNamespaceTheTargetPathDictates()
    {
        var project = Write("Widgets/Widgets.csproj", RazorProject("Widgets.Panels"));
        Write("Widgets/Panels/Card.razor", "<article>card</article>\n");

        var report = await Build(OptionsFor(project));

        report.ExitCode.Should().Be(0);
        var assembly = Assembly.LoadFrom(report.Projects.Single().Result!.AssemblyPath!);
        // AssignTargetPath's RootFolder is the project directory, so Panels/Card.razor is
        // <RootNamespace>.Panels.Card. Get the TargetPath wrong and this compiles into a namespace
        // nothing references — green, and useless.
        assembly.GetType("Widgets.Panels.Card").Should().NotBeNull();
    }

    [Fact]
    public async Task AnImportsFileContributesItsUsingsToASiblingComponent()
    {
        var project = Write("Widgets/Widgets.csproj", RazorProject("Widgets.Clock"));
        Write("Widgets/_Imports.razor", "@using System.Globalization\n");
        // Only _Imports.razor makes CultureInfo resolvable here — so if _Imports were dropped from
        // the item set (it is a .razor file like any other) this would not compile.
        Write("Widgets/Clock.razor", """
            <time>@CultureInfo.InvariantCulture.Name</time>
            """);

        var report = await Build(OptionsFor(project));

        report.ExitCode.Should().Be(0);
        Assembly.LoadFrom(report.Projects.Single().Result!.AssemblyPath!)
            .GetType("Widgets.Clock").Should().NotBeNull();
    }

    // ── the negatives: every silent failure gets a name ────────────────────────────────────────

    [Fact]
    public async Task WithNoRazorGeneratorTheBuildNAMESIT_ratherThanEmittingCS0115Noise()
    {
        var project = Write("Widgets/Widgets.csproj", RazorProject("Widgets.NoGenerator"));
        Write("Widgets/Spacer.razor", "<div>spacer</div>\n");
        var empty = Path.Combine(_root, "no-generators");
        Directory.CreateDirectory(empty);

        var options = OptionsFor(project) with { RazorGeneratorDirectory = empty };
        var report = await Build(options);

        report.ExitCode.Should().Be(1);
        var result = report.Projects.Single().Result!;
        result.Failure.Should().NotBeNull();
        result.Failure.Should().Contain("Microsoft.CodeAnalysis.Razor.Compiler",
            "the builder must name the generator that did not run");
        result.Failure.Should().Contain("CS0115",
            "and must say what the wall of errors would have been, so nobody debugs the source");
        // 🚨 ONE failure, not a wall. The point of naming the cause is that the symptom never gets
        // printed at all.
        result.Errors.Should().Be(1);
    }

    [Fact]
    public void APlainSdkProjectWithRazorFilesIsREFUSED_untilItIsAcknowledged()
    {
        var project = Write("Plain/Plain.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        Write("Plain/Thing.cs", "namespace Plain; public static class Thing { }");
        Write("Plain/Stray.razor", "<p>stray</p>\n");

        var refusal = Assert.Throws<ProjectFile.UnsupportedConstructException>(
            () => ProjectFile.Load(project));
        refusal.Message.Should().Contain("Stray.razor");
        refusal.Message.Should().Contain(ProjectFile.Accept.RazorNotCompiled);

        // Acknowledged, the project loads and carries NO Razor items — the SDK ignores them too.
        var model = ProjectFile.Load(project, [ProjectFile.Accept.RazorNotCompiled]);
        model.RazorItems.Should().BeEmpty();
    }

    [Fact]
    public void ScopedCssPairsTheComponentWithTheSdkScope_andTheOldAcceptStaysANoOp()
    {
        // This used to be a REFUSAL (the scope hash was an MSBuild task this builder could not
        // run); ScopedCss now reproduces that hash, pinned against SDK-built values
        // (ScopedCssTest), so evaluation pairs the component with its stylesheet's scope instead.
        var project = Write("Widgets/Widgets.csproj", RazorProject("Widgets.Scoped"));
        Write("Widgets/Spacer.razor", "<div>spacer</div>\n");
        Write("Widgets/Spacer.razor.css", ".spacer { height: 8px; }\n");
        Write("Widgets/Bare.razor", "<div>no stylesheet</div>\n");

        var model = ProjectFile.Load(project);
        var spacer = model.RazorItems.Single(i => i.Path.EndsWith("Spacer.razor"));
        spacer.CssScope.Should().Be(
            ScopedCss.GenerateScope("Spacer.razor.css", "Widgets.Scoped"),
            "the generator's markup attribute and the bundler's selector suffix are this one value");
        model.RazorItems.Single(i => i.Path.EndsWith("Bare.razor")).CssScope.Should().BeNull(
            "a component without a stylesheet sibling gets no scope, exactly like ApplyCssScopes");
        // The .razor.css itself is never a Razor input — it is a stylesheet, and the default
        // *.razor glob must not pick it up.
        model.RazorItems.Should().HaveCount(2);

        // A caller still passing the historical accept keeps building identically.
        ProjectFile.Load(project, [ProjectFile.Accept.RazorCssScope])
            .RazorItems.Single(i => i.Path.EndsWith("Spacer.razor"))
            .CssScope.Should().Be(spacer.CssScope);
    }

    [Fact]
    public void ContentRemoveTakesAComponentOutOfTheBuild()
    {
        var project = Write("Widgets/Widgets.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Razor">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <Content Remove="Legacy.razor" />
              </ItemGroup>
            </Project>
            """);
        Write("Widgets/Keep.razor", "<p>keep</p>\n");
        Write("Widgets/Legacy.razor", "<p>legacy</p>\n");

        var model = ProjectFile.Load(project);

        model.RazorItems.Select(i => i.TargetPath).Should().Equal("Keep.razor");
    }

    [Fact]
    public void TheRazorItemsCarryTheTargetPathTheSdkWouldHaveAssigned()
    {
        var project = Write("Widgets/Widgets.csproj", RazorProject("Widgets.TargetPaths"));
        Write("Widgets/Panels/Card.razor", "<article>card</article>\n");
        Write("Widgets/Views/Index.cshtml", "@{ }\n<p>index</p>\n");

        var model = ProjectFile.Load(project);

        model.RazorItems.Select(i => i.TargetPath).Order(StringComparer.Ordinal)
            .Should().Equal(Path.Combine("Panels", "Card.razor"), Path.Combine("Views", "Index.cshtml"));
        model.RazorItems.Single(i => i.TargetPath.EndsWith(".razor", StringComparison.Ordinal))
            .IsComponent.Should().BeTrue();
        model.RazorItems.Single(i => i.TargetPath.EndsWith(".cshtml", StringComparison.Ordinal))
            .IsComponent.Should().BeFalse();
    }

    [Fact]
    public void AMissingGeneratorDirectoryIsAFAILURE_neverAnEmptyGeneratorSet()
    {
        var absent = Path.Combine(_root, "gone");

        var refusal = Assert.Throws<RazorGenerators.MissingRazorCompilerException>(
            () => RazorGenerators.Load(absent, NullLogger.Instance));

        refusal.Message.Should().Contain(absent);
    }

    [Fact]
    public void ADirectoryOfNonGeneratorAssembliesIsAFAILURE_byName()
    {
        // An assembly that loads fine and carries no [Generator]: the "it is there, so it works"
        // assumption, refused.
        var directory = Path.Combine(_root, "not-generators");
        Directory.CreateDirectory(directory);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "MeshWeaver.ShortGuid.dll"),
            Path.Combine(directory, "MeshWeaver.ShortGuid.dll"));

        var refusal = Assert.Throws<RazorGenerators.MissingRazorCompilerException>(
            () => RazorGenerators.Load(directory, NullLogger.Instance));

        refusal.Message.Should().Contain("no usable Roslyn source generator");
    }

    [Fact]
    public void AnOperatorsDirectoryREPLACESTheSearchPath_itDoesNotHeadIt()
    {
        // 🚨 No fallback. A --razor-generators that turns out to be empty must FAIL, not quietly
        // pick up the image's own copy and report on a generator nobody named.
        RazorGenerators.SearchPath("/tmp/mine", "/app")
            .Should().AllSatisfy(p => p.Should().StartWith(Path.GetFullPath("/tmp/mine")));

        // Per-RID before flat, builder before /app — the layout a multi-arch image publishes read
        // in the order that makes the RID-specific copy win.
        var standard = RazorGenerators.SearchPath(null, "/app");
        standard[0].Should().Be(Path.Combine(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, RazorGenerators.DirectoryName)),
            RazorGenerators.RuntimeIdentifiers[0]));
        standard.Should().Contain(Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, RazorGenerators.DirectoryName)));
        standard[^1].Should().Be(Path.GetFullPath(
            Path.Combine("/app", RazorGenerators.DirectoryName)));
    }
}
