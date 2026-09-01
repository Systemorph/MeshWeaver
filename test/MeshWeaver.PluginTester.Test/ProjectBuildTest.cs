using System.Reactive;
using System.Reflection;
using MeshWeaver.Data;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// <c>mw-plugin-test build-project</c> end to end: a real project on disk, compiled against a real
/// reference set (this suite's own output directory, which has exactly the shape <c>/app</c> has),
/// emitted to a real DLL that is then LOADED — because "the compiler returned" is not evidence that
/// an assembly was produced.
///
/// <para>The diagnostic standard is asserted rather than assumed: a deliberately unresolvable
/// <c>cref</c> and a deliberately unused local both have to be REPORTED, or this builder is
/// producing something other than the build the SDK would have produced.</para>
/// </summary>
public class ProjectBuildTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mw-projectbuild-{Guid.NewGuid():N}");

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

    private const string LibraryProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <GenerateDocumentationFile>true</GenerateDocumentationFile>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>
    /// An <c>/app</c>-shaped directory built from the suite's own output: the REAL
    /// <c>mw-plugin-test.deps.json</c> (73 packages, the MeshWeaver assemblies at one binding
    /// identity) plus one assembly on disk. Everything else in the reference set arrives through
    /// the process's TPA, exactly as the shared framework does inside a container.
    ///
    /// <para>🚨 It is a copy rather than <c>AppContext.BaseDirectory</c> itself because a TEST bin
    /// directory carries THREE <c>*.deps.json</c> (the suite's, mw-plugin-test's, mw-combo-verify's)
    /// and the reference set correctly refuses an ambiguous one. That refusal is a feature under
    /// test, not an obstacle to work around. The <c>_container</c> name is deliberate too: macOS is
    /// case-insensitive, so a fixture called <c>app</c> IS the <c>App</c> project directory one of
    /// these tests writes, and the copy then silently lands in a directory full of .cs files.</para>
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

    private ProjectBuild.Options OptionsFor(string entry) =>
        new()
        {
            EntryProjects = [entry],
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "out"),
            Output = TextWriter.Null,
            MaxParallel = 4,
        };

    /// <summary>
    /// The one bridge in this suite, and it is the sanctioned one — <c>ObservableAwait.Await</c>,
    /// never Rx's blocking <c>Wait()</c>.
    /// </summary>
    private static Task<ProjectBuild.Report> Build(ProjectBuild.Options options) =>
        ProjectBuild.Run(options).Await(TestContext.Current.CancellationToken);

    // ── the happy path, proven by LOADING what it emitted ──────────────────────────────────────

    [Fact]
    public async Task ARealProjectCompilesAgainstTheContainerAndTheEmittedAssemblyLOADS()
    {
        var project = Write("Lib/Lib.csproj", LibraryProject);
        Write("Lib/Greeter.cs", """
            namespace Lib;
            /// <summary>Says hello.</summary>
            public static class Greeter
            {
                /// <summary>The greeting.</summary>
                /// <returns>A greeting.</returns>
                public static string Greet() => string.Join(' ', new[] { "hello", "mesh" });
            }
            """);

        var report = await Build(OptionsFor(project));

        report.FatalError.Should().BeNull();
        report.ExitCode.Should().Be(0);
        var result = report.Projects.Single().Result!;
        result.AssemblyPath.Should().NotBeNull();

        // 🚨 The claim is "it produced an assembly", so load it. A length check admits an image with
        // an unwritten region in it — MeshWeaver#1412's signature.
        var assembly = Assembly.LoadFrom(result.AssemblyPath!);
        var greeter = assembly.GetType("Lib.Greeter");
        greeter.Should().NotBeNull();
        greeter!.GetMethod("Greet")!.Invoke(null, null).Should().Be("hello mesh");

        // GenerateDocumentationFile=true, so the doc file is beside it under the ASSEMBLY's name.
        File.Exists(Path.Combine(Path.GetDirectoryName(result.AssemblyPath!)!, "Lib.xml")).Should().BeTrue();
    }

    [Fact]
    public async Task ImplicitUsingsAreSuppliedOrNothingRealCompiles()
    {
        // No `using System.Linq;` anywhere in the source: the SDK's generated global usings are the
        // only thing that makes this compile, so their absence is a silent behaviour change.
        var project = Write("Lib/Lib.csproj", LibraryProject);
        Write("Lib/Sums.cs", """
            namespace Lib;
            /// <summary>Sums.</summary>
            public static class Sums
            {
                /// <summary>Adds.</summary>
                /// <returns>The total.</returns>
                public static int Total() => new[] { 1, 2, 3 }.Sum();
            }
            """);

        (await Build(OptionsFor(project))).ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task APlatformTypeFromTheContainerIsInScopeWithoutAnyPackageOrProjectReference()
    {
        // The reference set is the CONTAINER, so a platform type compiles with nothing declared.
        var project = Write("Lib/Lib.csproj", LibraryProject);
        Write("Lib/UsesPlatform.cs", """
            namespace Lib;
            /// <summary>Touches a platform type.</summary>
            public static class UsesPlatform
            {
                /// <summary>Makes one.</summary>
                /// <returns>A log.</returns>
                public static MeshWeaver.Data.ActivityLog Make() => new("Compilation");
            }
            """);

        (await Build(OptionsFor(project))).ExitCode.Should().Be(0);
    }

    // ── the ProjectReference graph ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADependencyIsBuiltFirstAndItsOutputIsWhatTheDependentCompilesAgainst()
    {
        Write("Directory.Build.props", "<Project><PropertyGroup /></Project>");
        var app = Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Core/Core.csproj" /></ItemGroup>
            </Project>
            """);
        Write("App/Uses.cs", "namespace App; public static class Uses { public static int N => Core.Numbers.Answer; }");
        Write("Core/Core.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        Write("Core/Numbers.cs", "namespace Core; public static class Numbers { public const int Answer = 42; }");

        var report = await Build(OptionsFor(app));

        report.FatalError.Should().BeNull();
        report.ExitCode.Should().Be(0);
        report.Projects.Should().HaveCount(2);
        var core = report.Projects.Single(p => p.Id.EndsWith("Core.csproj", StringComparison.Ordinal));
        var appResult = report.Projects.Single(p => p.Id.EndsWith("App.csproj", StringComparison.Ordinal));
        // The cascade is the ordering: App becomes READY only once Core has finished.
        appResult.Ready.Should().BeGreaterThanOrEqualTo(core.Finished);
        Assembly.LoadFrom(appResult.Result!.AssemblyPath!).GetType("App.Uses").Should().NotBeNull();
    }

    [Fact]
    public async Task ACycleIsRefusedByNameBeforeAnythingIsCompiled()
    {
        Write("Directory.Build.props", "<Project><PropertyGroup /></Project>");
        var a = Write("A/A.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../B/B.csproj" /></ItemGroup>
            </Project>
            """);
        Write("A/One.cs", "namespace A; public class One;");
        Write("B/B.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../A/A.csproj" /></ItemGroup>
            </Project>
            """);
        Write("B/Two.cs", "namespace B; public class Two;");

        var report = await Build(OptionsFor(a));

        report.ExitCode.Should().Be(70);
        report.FatalError.Should().NotBeNull();
        report.FatalError!.Should().Contain("cycle");
    }

    [Fact]
    public async Task AProjectReferenceOutsideTheSourceRootComesFromTheContainer()
    {
        // The shape MeshWeaver.Plugins/src uses: ProjectReference $(MeshWeaverRoot)/src/… , which
        // does not exist in the image and must resolve to the assembly that does.
        Write("Directory.Build.props", "<Project><PropertyGroup /></Project>");
        var project = Write("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="/elsewhere/MeshWeaver.Data/MeshWeaver.Data.csproj" />
              </ItemGroup>
            </Project>
            """);
        Write("Lib/One.cs", "namespace Lib; public class One { public MeshWeaver.Data.ActivityLog L => new(\"Compilation\"); }");

        var report = await Build(OptionsFor(project));

        report.ExitCode.Should().Be(0);
        report.Projects.Should().ContainSingle();
    }

    [Fact]
    public async Task AProjectReferenceTheContainerCannotSupplyEitherIsRefusedByName()
    {
        Write("Directory.Build.props", "<Project><PropertyGroup /></Project>");
        var project = Write("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="/elsewhere/Totally.Absent/Totally.Absent.csproj" />
              </ItemGroup>
            </Project>
            """);
        Write("Lib/One.cs", "namespace Lib; public class One;");

        var report = await Build(OptionsFor(project));

        report.ExitCode.Should().Be(70);
        report.FatalError!.Should().Contain("Totally.Absent");
    }

    // ── references the container does not supply ───────────────────────────────────────────────

    [Fact]
    public async Task APackageTheContainerDoesNotSupplyIsNamed_NeverSkipped()
    {
        var project = Write("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="ClosedXML" Version="0.104.0" />
                <PackageReference Include="CsvHelper" Version="33.0.1" />
              </ItemGroup>
            </Project>
            """);
        Write("Lib/One.cs", "namespace Lib; public class One;");

        var report = await Build(OptionsFor(project));

        report.ExitCode.Should().Be(1);
        var result = report.Projects.Single().Result!;
        result.UnresolvedPackages.Should().Equal("ClosedXML", "CsvHelper");
        result.Failure!.Should().Contain("ClosedXML");
        // The diagnosis is in the ACTIVITY LOG, which is what a caller streams.
        report.Activity.Errors().Should().Contain(m => m.Message.Contains("ClosedXML"));
    }

    [Fact]
    public async Task AnAdditionalLibrarySuppliedWithExtraRefsSatisfiesThePackage()
    {
        var project = Write("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Extra.Library" Version="1.0.0" /></ItemGroup>
            </Project>
            """);
        Write("Lib/One.cs", "namespace Lib; public class One;");
        // A real assembly under the package's name is what "additional to the platform" looks like.
        var refs = Path.Combine(_root, "refs");
        Directory.CreateDirectory(refs);
        File.Copy(typeof(Cascade).Assembly.Location, Path.Combine(refs, "Extra.Library.dll"));

        var options = OptionsFor(project) with { ExtraReferenceDirectories = [refs] };

        (await Build(options)).ExitCode.Should().Be(0);
    }

    // ── the diagnostic standard ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnresolvableCrefIsREPORTED_BecauseDocumentationModeIsDiagnose()
    {
        // 🚨 CS1574 only exists when the parse options say Diagnose. A builder that dropped it would
        // pass content the platform's own -warnaserror gate rejects.
        var project = Write("Lib/Lib.csproj", LibraryProject);
        Write("Lib/BadCref.cs", """
            namespace Lib;
            /// <summary>See <see cref="NoSuchTypeAnywhere"/>.</summary>
            public class BadCref;
            """);

        var report = await Build(OptionsFor(project));

        report.ExitCode.Should().Be(1);
        report.Activity.Warnings().Should().Contain(m => m.Message.Contains("CS1574"));
    }

    [Fact]
    public async Task AnUnusedLocalIsREPORTED_AndTheNoWarnPolicyFailsTheBuildOnIt()
    {
        var project = Write("Lib/Lib.csproj", LibraryProject);
        Write("Lib/Unused.cs", """
            namespace Lib;
            /// <summary>Has an unused local.</summary>
            public static class Unused
            {
                /// <summary>Does nothing useful.</summary>
                public static void Go() { int neverRead = 1; }
            }
            """);

        var report = await Build(OptionsFor(project));

        report.ExitCode.Should().Be(1);
        report.Activity.Warnings().Should().Contain(m => m.Message.Contains("CS0219"));
        report.Projects.Single().Result!.Failure!.Should().Contain("no-warn policy");
    }

    [Fact]
    public async Task AllowWarningsIsTheDeliberateOptOut()
    {
        var project = Write("Lib/Lib.csproj", LibraryProject);
        Write("Lib/Unused.cs", """
            namespace Lib;
            /// <summary>Has an unused local.</summary>
            public static class Unused
            {
                /// <summary>Does nothing useful.</summary>
                public static void Go() { int neverRead = 1; }
            }
            """);

        var report = await Build(OptionsFor(project) with { AllowWarnings = true });

        report.ExitCode.Should().Be(0);
        report.Projects.Single().Result!.Warnings.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TheProjectsNoWarnSuppressesItsOwnDiagnostic()
    {
        var project = Write("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <NoWarn>$(NoWarn);CS0219</NoWarn>
              </PropertyGroup>
            </Project>
            """);
        Write("Lib/Unused.cs", "namespace Lib; public static class Unused { public static void Go() { int neverRead = 1; } }");

        var report = await Build(OptionsFor(project));

        report.ExitCode.Should().Be(0);
        report.Projects.Single().Result!.Warnings.Should().Be(0);
    }

    [Fact]
    public async Task NullableAnalysisIsOnWhenTheProjectSaysSo_AndOffWhenItDoesNot()
    {
        const string source = """
            namespace Lib;
            /// <summary>Returns null from a non-nullable signature.</summary>
            public static class Nulls
            {
                /// <summary>Lies.</summary>
                /// <returns>Null.</returns>
                public static string Get() => null!;
                /// <summary>Also lies, without the suppression.</summary>
                /// <returns>Null.</returns>
                public static string Honest() { string? maybe = null; return maybe; }
            }
            """;
        var enabled = Write("On/On.csproj", LibraryProject);
        Write("On/Nulls.cs", source);
        (await Build(OptionsFor(enabled))).Activity.Warnings()
            .Should().Contain(m => m.Message.Contains("CS8603"));

        var disabled = Write("Off/Off.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>disable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        // The same shape without the `?` annotation, which is CS8632 under nullable-disable and
        // would prove a different point.
        Write("Off/Nulls.cs", source.Replace("string? maybe", "string maybe"));
        (await Build(OptionsFor(disabled))).ExitCode.Should().Be(0);
    }

    // ── fail closed ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARunThatCompilesNothingIsAFailure()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Nothing"));

        var report = await Build(OptionsFor(Path.Combine(_root, "Nothing")));

        report.ExitCode.Should().Be(70);
        report.FatalError!.Should().Contain("no .csproj");
    }

    [Fact]
    public async Task AnUnreadableContainerStopsTheRunBeforeAnyCompile()
    {
        var project = Write("Lib/Lib.csproj", LibraryProject);
        Write("Lib/One.cs", "namespace Lib; public class One;");

        var report = await Build(OptionsFor(project) with
        {
            AppDirectory = Path.Combine(_root, "not-a-container"),
        });

        report.ExitCode.Should().Be(70);
        report.FatalError!.Should().Contain("does not exist");
    }

    [Fact]
    public async Task ATargetInTheProjectStopsTheRunUntilItIsAcknowledged()
    {
        var project = Write("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Target Name="VerifyAssemblyVersionMatchesPlatform" BeforeTargets="CoreCompile" />
            </Project>
            """);
        Write("Lib/One.cs", "namespace Lib; public class One;");

        (await Build(OptionsFor(project))).ExitCode.Should().Be(70);
        (await Build(OptionsFor(project) with { Accept = ["target:VerifyAssemblyVersionMatchesPlatform"] }))
            .ExitCode.Should().Be(0);
    }

    // ── streaming ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryDiagnosticReachesTheObserverAsItIsProduced_NotBatchedAtTheEnd()
    {
        var project = Write("Lib/Lib.csproj", LibraryProject);
        Write("Lib/Unused.cs", """
            namespace Lib;
            /// <summary>Has an unused local.</summary>
            public static class Unused
            {
                /// <summary>Does nothing useful.</summary>
                public static void Go() { int neverRead = 1; }
            }
            """);
        var streamed = new List<LogMessage>();
        var completed = false;

        var report = await ProjectBuild.Run(OptionsFor(project) with
        {
            Log = Observer.Create<LogMessage>(streamed.Add, _ => { }, () => completed = true),
        }).Await(TestContext.Current.CancellationToken);

        completed.Should().BeTrue();
        streamed.Should().Contain(m => m.Message.Contains("CS0219"));
        // The activity log is the same stream, sealed — the record a caller keeps.
        report.Activity.Category.Should().Be(ActivityCategory.Compilation);
        report.Activity.Status.Should().Be(ActivityStatus.Failed);
        report.Activity.Messages.Count.Should().Be(streamed.Count);
        // Diagnostics carry their severity, so the reader can act on them without re-parsing text.
        streamed.Single(m => m.Message.Contains("CS0219")).LogLevel.Should().Be(LogLevel.Warning);
    }

}
