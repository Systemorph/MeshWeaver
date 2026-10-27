using System.Reactive;
using MeshWeaver.Data;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The <c>--prebuilt</c> seam: an in-root <c>ProjectReference</c> whose assembly this run (or a
/// sibling job) already built resolves to that DLL and is NOT rebuilt — "we don't need to rebuild
/// the mesh" (maintainer, 2026-08-31). Measured motivation: every AI-family module job re-compiled
/// the 168-source MeshWeaver.AI its own run's floor job had already built (~3 minutes per
/// dependent); with the seam the dependent compiles in seconds.
/// </summary>
public class PrebuiltSiblingTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mw-prebuilt-{Guid.NewGuid():N}");

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

    [Fact]
    public async Task AnInRootReferenceWithAPrebuiltCopyIsConsumedNotRebuilt()
    {
        // The "sibling module": built once, its output handed back as --prebuilt.
        Write("src/Directory.Build.props", "<Project></Project>");
        var sibling = Write("src/Widgets.Sibling/Widgets.Sibling.csproj", LibProject);
        Write("src/Widgets.Sibling/Api.cs",
            "namespace Widgets.Sibling; public static class Api { public static int Answer() => 42; }");
        var consumer = Write("src/Widgets.Consumer/Widgets.Consumer.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Widgets.Sibling/Widgets.Sibling.csproj" />
              </ItemGroup>
            </Project>
            """);
        Write("src/Widgets.Consumer/Uses.cs",
            "namespace Widgets.Consumer; public static class Uses { public static int Get() => Widgets.Sibling.Api.Answer(); }");

        var first = await ProjectBuild.Run(new()
        {
            EntryProjects = [sibling],
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "out1"),
            Output = TextWriter.Null,
            MaxParallel = 2,
        }).Await(TestContext.Current.CancellationToken);
        first.ExitCode.Should().Be(0);

        var second = await ProjectBuild.Run(new()
        {
            EntryProjects = [consumer],
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "out2"),
            Output = TextWriter.Null,
            MaxParallel = 2,
            PrebuiltDirectories = [Path.Combine(_root, "out1", "Widgets.Sibling")],
        }).Await(TestContext.Current.CancellationToken);

        second.ExitCode.Should().Be(0);
        second.Projects.Select(r => r.Result!.AssemblyName)
            .Should().Equal(new[] { "Widgets.Consumer" },
                "the sibling resolved PREBUILT and must not appear in the build order — "
                + "rebuilding the mesh per dependent is the waste this seam removes");
    }

    /// <summary>
    /// <c>--dependency-edges</c> with <c>--prebuilt</c>: the consumer's USED reference to a prebuilt
    /// sibling is an edge, although the sibling is not in this run's build results — an edge set
    /// missing it would be presented as complete and could narrow CI selection wrongly. The sibling
    /// gets no edge set of its own (this run did not compile it, so it read none of its edges), and
    /// the negative control is that a prebuilt sibling the consumer declares but never USES is not
    /// an edge.
    /// </summary>
    [Fact]
    public async Task DependencyEdgesNameAUsedPrebuiltSibling_AndGiveItNoSetOfItsOwn()
    {
        Write("src/Directory.Build.props", "<Project></Project>");
        var sibling = Write("src/Widgets.Sibling/Widgets.Sibling.csproj", LibProject);
        Write("src/Widgets.Sibling/Api.cs",
            "namespace Widgets.Sibling; public static class Api { public static int Answer() => 42; }");
        var idle = Write("src/Widgets.Idle/Widgets.Idle.csproj", LibProject);
        Write("src/Widgets.Idle/Other.cs",
            "namespace Widgets.Idle; public static class Other { public static int Nothing() => 0; }");
        var consumer = Write("src/Widgets.Consumer/Widgets.Consumer.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Widgets.Sibling/Widgets.Sibling.csproj" />
                <ProjectReference Include="../Widgets.Idle/Widgets.Idle.csproj" />
              </ItemGroup>
            </Project>
            """);
        Write("src/Widgets.Consumer/Uses.cs",
            "namespace Widgets.Consumer; public static class Uses { public static int Get() => Widgets.Sibling.Api.Answer(); }");

        var first = await ProjectBuild.Run(new()
        {
            EntryProjects = [sibling, idle],
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "out1"),
            Output = TextWriter.Null,
            MaxParallel = 2,
        }).Await(TestContext.Current.CancellationToken);
        first.ExitCode.Should().Be(0, first.FatalError ?? "");

        var second = await ProjectBuild.Run(new()
        {
            EntryProjects = [consumer],
            AppDirectory = ProjectBuildTest.WithCompatibilityAnchor(AppDirectory()),
            OutputDirectory = Path.Combine(_root, "out2"),
            Output = TextWriter.Null,
            MaxParallel = 2,
            DependencyEdges = true,
            PrebuiltDirectories =
            [
                Path.Combine(_root, "out1", "Widgets.Sibling"),
                Path.Combine(_root, "out1", "Widgets.Idle"),
            ],
        }).Await(TestContext.Current.CancellationToken);
        second.ExitCode.Should().Be(0, second.FatalError ?? "");
        second.Projects.Select(r => r.Result!.AssemblyName).Should().Equal(new[] { "Widgets.Consumer" },
            "both siblings resolved PREBUILT — the combination under test");

        var document = DependencyEdgesFile.Deserialize(
            File.ReadAllText(Path.Combine(_root, "out2", DependencyEdgesFile.FileName)));
        document.Should().NotBeNull();
        document!.Sets.Select(s => s.Source).Should().Equal(new[] { "Widgets.Consumer" },
            "a prebuilt sibling was not compiled here, so it carries no edge set");
        document.Sets.Single().Edges.Select(e => e.Target).Should().Equal(new[] { "Widgets.Sibling" },
            "the used prebuilt sibling is an edge; the declared-but-unused one is not");
    }

    [Fact]
    public async Task AMissingPrebuiltDirectoryIsARefusalNotASilentRebuild()
    {
        Write("src/Directory.Build.props", "<Project></Project>");
        var entry = Write("src/Widgets.Lone/Widgets.Lone.csproj", LibProject);
        Write("src/Widgets.Lone/A.cs", "namespace Widgets.Lone; public static class A { }");

        var report = await ProjectBuild.Run(new()
        {
            EntryProjects = [entry],
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "out"),
            Output = TextWriter.Null,
            PrebuiltDirectories = [Path.Combine(_root, "not-there")],
        }).Await(TestContext.Current.CancellationToken);

        report.FatalError.Should().Contain("--prebuilt",
            "a prebuilt directory that is not there silently rebuilds everything it was supposed "
            + "to supply — wasted work wearing a green log");
    }
}
