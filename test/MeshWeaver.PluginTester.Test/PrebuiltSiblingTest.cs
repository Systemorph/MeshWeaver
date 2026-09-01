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
