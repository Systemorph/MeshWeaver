using System.Reactive;
using MeshWeaver.Data;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// 🚨 <b>The seam for a type that MOVES OUT of an image-shipped assembly into a module in the same
/// repository</b> (<c>--superseded-image-assembly</c>).
///
/// <para>The reference set is the whole container MINUS <c>Graph.ShadowedAssemblyNames</c>, and that
/// set holds only what the run compiles FROM SOURCE — membership being reachability-driven through
/// <c>ProjectReference</c> edges. So the edge that would have shadowed the image's stale copy is
/// exactly the edge such a move DELETES: the image still defines the moved type, the module defines
/// it too, and the compile dies <c>CS0436</c>. Measured on MeshWeaver.Plugins#1268, where three Razor
/// views left <c>MeshWeaver.Blazor.Views</c> for <c>MeshWeaver.Markdown.Collaboration</c>.</para>
///
/// <para>Both arms are asserted here. A test that only proved the option makes a build green would
/// not distinguish "the drop worked" from "there was never a collision" — so the control builds the
/// identical tree WITHOUT the option and requires it to FAIL naming the type.</para>
/// </summary>
public class SupersededImageAssemblyTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mw-superseded-{Guid.NewGuid():N}");

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

    private string AppDirectory()
    {
        var app = Path.Combine(_root, "_container");
        if (Directory.Exists(app)) return app;
        Directory.CreateDirectory(app);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "mw-plugin-test.deps.json"),
            Path.Combine(app, "mw-plugin-test.deps.json"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "MeshWeaver.ShortGuid.dll"),
            Path.Combine(app, "MeshWeaver.ShortGuid.dll"));
        return app;
    }

    private const string LibProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>
    /// Stages the container the way the platform image stages <c>/app</c>: an assembly named
    /// <c>Legacy.Home</c> that still defines <c>Moved.Thing</c> — the state an image is in for one
    /// wave after the type left it in source.
    /// </summary>
    private async Task<string> StageImageWithTheOldCopy(CancellationToken ct)
    {
        Write("old/Directory.Build.props", "<Project></Project>");
        var old = Write("old/Legacy.Home/Legacy.Home.csproj", LibProject);
        Write("old/Legacy.Home/Thing.cs",
            "namespace Moved; public sealed class Thing { public int Value => 1; }");

        var built = await ProjectBuild.Run(new()
        {
            EntryProjects = [old],
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "image-build"),
            Output = TextWriter.Null,
        }).Await(ct);
        built.ExitCode.Should().Be(0, "staging the container is setup, not the assertion");

        var app = AppDirectory();
        File.Copy(
            Path.Combine(_root, "image-build", "Legacy.Home", "Legacy.Home.dll"),
            Path.Combine(app, "Legacy.Home.dll"), overwrite: true);
        return app;
    }

    /// <summary>The module that now owns the type: same namespace, same name, new assembly.</summary>
    private string TheModuleThatNowOwnsIt()
    {
        Write("src/Directory.Build.props", "<Project></Project>");
        var module = Write("src/New.Owner/New.Owner.csproj", LibProject);
        Write("src/New.Owner/Thing.cs",
            "namespace Moved; public sealed class Thing { public int Value => 2; }");
        // 🚨 The collision needs the NAME RESOLVED, not merely declared. CS0436 fires where the
        // compiler binds `Moved.Thing` and finds it in both the source and an imported assembly;
        // a declaration alone conflicts with nothing. In the real case (Plugins#1268) the Razor
        // source generator's `*_razor.g.cs` and the `.razor.cs` code-behind each bind the type,
        // which is why it surfaced there. Reproducing that binding is what makes the control arm
        // able to fail — the first version of this test omitted it and the control passed,
        // proving nothing.
        Write("src/New.Owner/Uses.cs",
            "namespace New.Owner; public static class Uses { public static int V => new Moved.Thing().Value; }");
        return module;
    }

    [Fact]
    public async Task WithoutTheOption_TheImagesStaleCopyCollidesWithTheMovedType()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await StageImageWithTheOldCopy(ct);
        var module = TheModuleThatNowOwnsIt();

        // The builder's per-project `Failure` carries only a summary ("1 warning(s) under the
        // no-warn policy"), so the diagnostic CODE is asserted against the builder's own output —
        // otherwise this arm would pass on any failure at all, which is the whole thing it exists
        // to rule out.
        var narration = new StringWriter();
        var report = await ProjectBuild.Run(new()
        {
            EntryProjects = [module],
            AppDirectory = app,
            OutputDirectory = Path.Combine(_root, "out-control"),
            Output = narration,
        }).Await(ct);

        report.ExitCode.Should().NotBe(0,
            "this is the control arm — the image still defines Moved.Thing, so the compile MUST "
            + "collide. If this ever passes, the other test proves nothing.");
        narration.ToString().Should().Contain("CS0436",
            "the collision must be the imported-type conflict, not some other failure");
    }

    [Fact]
    public async Task DroppingTheSupersededAssemblyLetsTheMovedTypeCompile()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await StageImageWithTheOldCopy(ct);
        var module = TheModuleThatNowOwnsIt();

        var report = await ProjectBuild.Run(new()
        {
            EntryProjects = [module],
            AppDirectory = app,
            OutputDirectory = Path.Combine(_root, "out"),
            Output = TextWriter.Null,
            SupersededImageAssemblies = ["Legacy.Home"],
        }).Await(ct);

        report.ExitCode.Should().Be(0,
            "the image's superseded copy is dropped from the reference set, so the module's own "
            + "definition is the only one in scope");
        report.Projects.Select(p => p.Result!.AssemblyName)
            .Should().Equal(new[] { "New.Owner" });
    }

    [Fact]
    public async Task AnEmptyNameIsARefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var module = TheModuleThatNowOwnsIt();

        var report = await ProjectBuild.Run(new()
        {
            EntryProjects = [module],
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "out-empty"),
            Output = TextWriter.Null,
            SupersededImageAssemblies = ["   "],
        }).Await(ct);

        report.ExitCode.Should().NotBe(0,
            "an option that silently does nothing reads exactly like an option that worked");
    }

    [Fact]
    public async Task AnAssemblyTheImageDoesNotCarryIsARefusal_BecauseTheEntryIsWrongOrStale()
    {
        var ct = TestContext.Current.CancellationToken;
        var module = TheModuleThatNowOwnsIt();

        var report = await ProjectBuild.Run(new()
        {
            EntryProjects = [module],
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "out-absent"),
            Output = TextWriter.Null,
            SupersededImageAssemblies = ["Not.In.The.Image"],
        }).Await(ct);

        report.ExitCode.Should().NotBe(0,
            "either the name is wrong, or the image already stopped shipping it and the entry is "
            + "STALE — the case an allow-shaped input rots in silently unless it fails closed");
    }

    [Fact]
    public async Task AnAssemblyThisRunAlreadyBuildsIsARefusal_TheOptionWouldBeRedundant()
    {
        var ct = TestContext.Current.CancellationToken;
        var module = TheModuleThatNowOwnsIt();

        var report = await ProjectBuild.Run(new()
        {
            EntryProjects = [module],
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "out-redundant"),
            Output = TextWriter.Null,
            SupersededImageAssemblies = ["New.Owner"],
        }).Await(ct);

        report.ExitCode.Should().NotBe(0,
            "a project in the graph is shadowed anyway; naming it signals a misunderstanding of "
            + "what the option is for, and a silent no-op would leave that misunderstanding in place");
    }

    // ── the fourth refusal: the entry that OUTLIVED its reason (#3223) ─────────────────────────

    /// <summary>
    /// The image ONE WAVE LATER: <c>Legacy.Home</c> was rebuilt after the type left it, so the
    /// assembly is still shipped and still full of types — it just no longer defines the one the
    /// entry exists to shadow. This is the state no shape-check can see: the name is not empty, the
    /// container carries it, and the run does not build it.
    /// </summary>
    private async Task<string> StageImageRebuiltAfterTheMove(CancellationToken ct)
    {
        Write("rebuilt/Directory.Build.props", "<Project></Project>");
        var rebuilt = Write("rebuilt/Legacy.Home/Legacy.Home.csproj", LibProject);
        Write("rebuilt/Legacy.Home/Retired.cs",
            "namespace Legacy; public sealed class Retired { public int Value => 1; }");

        var built = await ProjectBuild.Run(new()
        {
            EntryProjects = [rebuilt],
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "image-rebuild"),
            Output = TextWriter.Null,
        }).Await(ct);
        built.ExitCode.Should().Be(0, "staging the container is setup, not the assertion");

        var app = AppDirectory();
        File.Copy(
            Path.Combine(_root, "image-rebuild", "Legacy.Home", "Legacy.Home.dll"),
            Path.Combine(app, "Legacy.Home.dll"), overwrite: true);
        return app;
    }

    [Fact]
    public async Task AStaleEntryIsARefusal_TheImageWasRebuiltAndThereIsNothingLeftToSupersede()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await StageImageRebuiltAfterTheMove(ct);
        var module = TheModuleThatNowOwnsIt();

        var report = await ProjectBuild.Run(new()
        {
            EntryProjects = [module],
            AppDirectory = app,
            OutputDirectory = Path.Combine(_root, "out-stale"),
            Output = TextWriter.Null,
            SupersededImageAssemblies = ["Legacy.Home"],
        }).Await(ct);

        // 🚨 The build would otherwise be GREEN — nothing collides any more, which is exactly why
        // an entry rots here in silence. The refusal is the whole point.
        report.ExitCode.Should().NotBe(0,
            "the image's copy no longer defines anything this repository declares, so the entry has "
            + "done its job and keeping it only subtracts a real assembly from the reference set");
        report.FatalError.Should().NotBeNull();
        report.FatalError.Should().Contain("STALE").And.Contain("Legacy.Home",
            "an entry that must be deleted has to be NAMED — a verdict nobody can act on is a "
            + "warning that scrolls past");
    }

    [Fact]
    public async Task AnEntryThatIsStillNeededStaysQUIET_TheOtherControlArm()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await StageImageWithTheOldCopy(ct);
        var module = TheModuleThatNowOwnsIt();

        var narration = new StringWriter();
        var report = await ProjectBuild.Run(new()
        {
            EntryProjects = [module],
            AppDirectory = app,
            OutputDirectory = Path.Combine(_root, "out-needed"),
            Output = narration,
            SupersededImageAssemblies = ["Legacy.Home"],
        }).Await(ct);

        report.ExitCode.Should().Be(0,
            "the image's copy still defines Moved.Thing, so the entry is holding a real collision "
            + "apart — a staleness check that fires here would break the one wave it exists for");
        narration.ToString().Should().Contain("'Legacy.Home' is still needed")
            .And.Contain("Moved.Thing",
                "the quiet arm must SAY what it measured — a check whose passing verdict is silence "
                + "cannot be distinguished from a check that never ran");
    }

    [Fact]
    public async Task ANarrowedRunDoesNotCallTheEntryStale_TheSourceSideIsTheREPOSITORY()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await StageImageWithTheOldCopy(ct);
        TheModuleThatNowOwnsIt();
        // A second module in the same repository — the only one this run compiles. The pack lane
        // narrows exactly like this: a PR-scoped diff, and a build ledger that hands back reused
        // bundles, routinely leave the module that owns the moved type OUT of the selection.
        var unrelated = Write("src/Other.Module/Other.Module.csproj", LibProject);
        Write("src/Other.Module/Widget.cs",
            "namespace Other; public sealed class Widget { public int Value => 3; }");

        var narration = new StringWriter();
        var report = await ProjectBuild.Run(new()
        {
            EntryProjects = [unrelated],
            AppDirectory = app,
            OutputDirectory = Path.Combine(_root, "out-narrowed"),
            Output = narration,
            SupersededImageAssemblies = ["Legacy.Home"],
        }).Await(ct);

        // 🚨 Reading "this run's compilations" as the source side would red THIS — an ordinary
        // narrowed PR, during the one wave the entry is needed. The source side is the repository.
        report.ExitCode.Should().Be(0,
            "New.Owner is not in this run's graph, but it IS in the repository — the entry is still "
            + "needed and a narrowed selection must never be read as staleness");
        narration.ToString().Should().Contain("'Legacy.Home' is still needed");
    }

    /// <summary>
    /// 🚨 The move that motivated the whole option (MeshWeaver.Plugins#1268) was three
    /// <c>.razor</c> views, and a Razor component's type exists in no <c>.cs</c> file at all. A
    /// staleness check that indexed only C# would therefore call the entry stale in precisely the
    /// case it was built for — the false red that would have made the whole check unusable.
    /// </summary>
    [Fact]
    public async Task ARazorComponentCountsAsSourceThatDefinesTheType_ElseTheMotivatingCaseFalseReds()
    {
        var ct = TestContext.Current.CancellationToken;

        // The image still ships the component's old home: same generated name, same namespace.
        Write("old/Directory.Build.props", "<Project></Project>");
        var old = Write("old/Legacy.Home/Legacy.Home.csproj", LibProject);
        Write("old/Legacy.Home/Card.cs",
            "namespace Widgets.Views; public sealed class Card { public int Value => 1; }");
        var staged = await ProjectBuild.Run(new()
        {
            EntryProjects = [old],
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "image-razor"),
            Output = TextWriter.Null,
        }).Await(ct);
        staged.ExitCode.Should().Be(0, "staging the container is setup, not the assertion");
        var app = AppDirectory();
        File.Copy(
            Path.Combine(_root, "image-razor", "Legacy.Home", "Legacy.Home.dll"),
            Path.Combine(app, "Legacy.Home.dll"), overwrite: true);

        // The module that now owns it — a .razor file and nothing else. RootNamespace + the
        // TargetPath directory is what the Razor SDK turns into `Widgets.Views.Card`.
        Write("src/Directory.Build.props", "<Project></Project>");
        var module = Write("src/Razor.Owner/Razor.Owner.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Razor">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AssemblyName>Razor.Owner</AssemblyName>
                <RootNamespace>Widgets</RootNamespace>
              </PropertyGroup>
            </Project>
            """);
        Write("src/Razor.Owner/Views/Card.razor", "<div class=\"card\">card</div>");

        var narration = new StringWriter();
        var report = await ProjectBuild.Run(new()
        {
            EntryProjects = [module],
            AppDirectory = app,
            OutputDirectory = Path.Combine(_root, "out-razor"),
            Output = narration,
            SupersededImageAssemblies = ["Legacy.Home"],
        }).Await(ct);

        narration.ToString().Should().NotContain("STALE",
            "the component IS this repository's definition of Widgets.Views.Card — it just lives in "
            + "a .razor file, which is the shape the option exists for");
        narration.ToString().Should().Contain("'Legacy.Home' is still needed")
            .And.Contain("Widgets.Views.Card");
        report.ExitCode.Should().Be(0);
    }

    /// <summary>Stages <c>Legacy.Home</c> in the container defining exactly the given C# source.</summary>
    private async Task<string> StageImageDefining(string tag, string source, CancellationToken ct)
    {
        Write($"{tag}/Directory.Build.props", "<Project></Project>");
        var project = Write($"{tag}/Legacy.Home/Legacy.Home.csproj", LibProject);
        Write($"{tag}/Legacy.Home/Defined.cs", source);
        var built = await ProjectBuild.Run(new()
        {
            EntryProjects = [project],
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, $"image-{tag}"),
            Output = TextWriter.Null,
        }).Await(ct);
        built.ExitCode.Should().Be(0, "staging the container is setup, not the assertion");
        var app = AppDirectory();
        File.Copy(
            Path.Combine(_root, $"image-{tag}", "Legacy.Home", "Legacy.Home.dll"),
            Path.Combine(app, "Legacy.Home.dll"), overwrite: true);
        return app;
    }

    /// <summary>
    /// A Razor class library whose components live in <c>Components/</c> under a root namespace
    /// that has nothing to do with the namespace they are generated into.
    /// </summary>
    private const string MovedViewsProject = """
        <Project Sdk="Microsoft.NET.Sdk.Razor">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <AssemblyName>Moved.Views</AssemblyName>
            <RootNamespace>Moved.Views</RootNamespace>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>
    /// 🚨 <b>The real shape of the move.</b> <c>CS0436</c> needs the SAME fully-qualified name on
    /// both sides, so a view that leaves an image-shipped assembly KEEPS its old namespace with an
    /// <c>@namespace</c> directive — its new folder then says nothing about its namespace, and the
    /// directory-suffix rule alone cannot see it. The control below is the same tree with the
    /// directive removed, which IS stale: that is what makes this arm attributable to the directive
    /// rather than to the suffix rule quietly matching anyway.
    /// </summary>
    [Fact]
    public async Task ARazorNamespaceDirectiveIsHonoured_AMovedViewKeepsItsOldNamespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await StageImageDefining(
            "ns-directive",
            "namespace Legacy.Blazor.Views; public sealed class Panel { public int Value => 1; }",
            ct);

        Write("src/Directory.Build.props", "<Project></Project>");
        var module = Write("src/Moved.Views/Moved.Views.csproj", MovedViewsProject);
        Write("src/Moved.Views/Components/Panel.razor", """
            @namespace Legacy.Blazor.Views

            <div class="panel">panel</div>
            """);

        var narration = new StringWriter();
        var report = await ProjectBuild.Run(new()
        {
            EntryProjects = [module],
            AppDirectory = app,
            OutputDirectory = Path.Combine(_root, "out-ns-directive"),
            Output = narration,
            SupersededImageAssemblies = ["Legacy.Home"],
        }).Await(ct);

        narration.ToString().Should().NotContain("STALE");
        narration.ToString().Should().Contain("'Legacy.Home' is still needed")
            .And.Contain("Legacy.Blazor.Views.Panel");
        report.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task WithoutTheDirectiveTheSameTreeISStale_TheControlThatMakesThePreviousArmMeanSomething()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await StageImageDefining(
            "ns-nodirective",
            "namespace Legacy.Blazor.Views; public sealed class Panel { public int Value => 1; }",
            ct);

        Write("src/Directory.Build.props", "<Project></Project>");
        var module = Write("src/Moved.Views/Moved.Views.csproj", MovedViewsProject);
        // No @namespace: the component is generated into Moved.Views.Components, which is a
        // DIFFERENT type from the image's Legacy.Blazor.Views.Panel — nothing collides, so the
        // entry really has nothing left to supersede.
        Write("src/Moved.Views/Components/Panel.razor", "<div class=\"panel\">panel</div>");

        var report = await ProjectBuild.Run(new()
        {
            EntryProjects = [module],
            AppDirectory = app,
            OutputDirectory = Path.Combine(_root, "out-ns-nodirective"),
            Output = TextWriter.Null,
            SupersededImageAssemblies = ["Legacy.Home"],
        }).Await(ct);

        report.FatalError.Should().NotBeNull();
        report.FatalError.Should().Contain("STALE").And.Contain("Legacy.Home");
    }

    [Fact]
    public async Task An_ImportsRazorNamespaceCoversTheFolderBeneathIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = await StageImageDefining(
            "ns-imports",
            "namespace Legacy.Blazor.Views.Inner; public sealed class Panel { public int Value => 1; }",
            ct);

        Write("src/Directory.Build.props", "<Project></Project>");
        var module = Write("src/Moved.Views/Moved.Views.csproj", MovedViewsProject);
        // The SDK applies an _Imports.razor namespace to the folder AND appends the path below it,
        // so Components/Inner/Panel.razor lands in Legacy.Blazor.Views.Inner.
        Write("src/Moved.Views/Components/_Imports.razor", "@namespace Legacy.Blazor.Views");
        Write("src/Moved.Views/Components/Inner/Panel.razor", "<div class=\"panel\">panel</div>");

        var narration = new StringWriter();
        var report = await ProjectBuild.Run(new()
        {
            EntryProjects = [module],
            AppDirectory = app,
            OutputDirectory = Path.Combine(_root, "out-ns-imports"),
            Output = narration,
            SupersededImageAssemblies = ["Legacy.Home"],
        }).Await(ct);

        narration.ToString().Should().NotContain("STALE");
        narration.ToString().Should().Contain("Legacy.Blazor.Views.Inner.Panel");
        report.ExitCode.Should().Be(0);
    }
}
