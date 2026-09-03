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
}
