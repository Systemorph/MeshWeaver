using System.Reactive;
using MeshWeaver.Data;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// <c>mw-plugin-test build-project</c> runs the SDK's BUILT-IN generators — the whole chain,
/// executed: the <c>sdk-generators/</c> directory the image SHIPS is found beside the builder with
/// no flag, loaded against THIS repo's Roslyn, and run over a real <c>[GeneratedRegex]</c> partial
/// whose body only the generator can supply.
///
/// <para>🚨 The failure this guards is SILENT in the image and LOUD only here. These generators
/// live in the SDK's targeting pack, not the runtime an image ships, so an image staged without
/// them fails every <c>[GeneratedRegex]</c> project as CS8795 — indistinguishable, from the module
/// lane's side, from "this project has errors" (measured on MeshWeaver.Markdown.Collaboration,
/// 2026-08-31: 7 errors → 0 with exactly this generator supplied). Like <see cref="RazorBuildTest"/>
/// gates the Razor pairing, this test EXECUTES the staged copy so a ref pack whose generator cannot
/// run against this repo's Roslyn turns the PR red instead of shipping a silent image.</para>
/// </summary>
public class SdkGeneratorBuildTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mw-sdkgen-{Guid.NewGuid():N}");

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
    /// The same <c>/app</c>-shaped fixture <see cref="RazorBuildTest"/> uses: the REAL shipped
    /// <c>deps.json</c> plus one assembly, everything else arriving through the process's TPA and
    /// the shared frameworks — which is where <c>System.Text.RegularExpressions</c> comes from,
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

    private static Task<ProjectBuild.Report> Build(ProjectBuild.Options options) =>
        ProjectBuild.Run(options).Await(TestContext.Current.CancellationToken);

    [Fact]
    public void TheImageShipsTheSdkGeneratorsBesideTheBuilder()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, ProjectBuild.SdkGeneratorDirectoryName);
        Directory.Exists(directory).Should().BeTrue(
            "the image build stages the SDK's built-in generators beside the builder; without them "
            + "every [GeneratedRegex] project fails CS8795 under build-project while compiling fine "
            + "on the SDK path — the exact split-brain the container lane exists to remove");

        var assemblies = Directory.GetFiles(directory, "*.dll")
            .Select(Path.GetFileName)
            .ToArray();
        assemblies.Should().Contain("System.Text.RegularExpressions.Generator.dll",
            "the regex generator is the one the module lane's green list is measured against");

        File.Exists(Path.Combine(directory, "sdk-generators.json")).Should().BeTrue(
            "the image must say WHICH ref pack it staged from — the image is the pin, and a pin "
            + "nobody can read is not a pin");
    }

    /// <summary>
    /// 🚨 The build IS the assertion: without the staged generator this partial has no body and the
    /// compile fails (CS8795), so a green report proves the generator was found with NO flag,
    /// loaded against this repo's Roslyn, and executed.
    /// </summary>
    [Fact]
    public async Task AGeneratedRegexProjectCompilesGreenWithNoGeneratorsFlag()
    {
        var entry = Write("Regexy/Regexy.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AssemblyName>Widgets.Regexy</AssemblyName>
                <RootNamespace>Widgets</RootNamespace>
              </PropertyGroup>
            </Project>
            """);
        Write("Regexy/Patterns.cs", """
            using System.Text.RegularExpressions;

            namespace Widgets;

            public static partial class Patterns
            {
                [GeneratedRegex("^a+b$")]
                public static partial Regex AaB();
            }
            """);

        var report = await Build(new()
        {
            EntryProject = entry,
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "out"),
            Output = TextWriter.Null,
            MaxParallel = 2,
        });

        report.FatalError.Should().BeNull();
        report.ExitCode.Should().Be(0,
            "a CS8795 here means the staged sdk-generators/ copy was not found or did not run — "
            + "the exact silent gap this test exists to keep red");
        var result = report.Projects.Single().Result!;
        result.Failure.Should().BeNull();
        result.Errors.Should().Be(0);
        result.IsGreen.Should().BeTrue();
    }
}
