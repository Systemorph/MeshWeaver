using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The csproj evaluator's contract — what <c>mw-plugin-test build-project</c> reads out of a
/// project when there is no MSBuild to read it.
///
/// <para>Every case here is one of the two failure modes the evaluator exists to prevent: a
/// project setting silently DROPPED (a build that looks green and is not the build the SDK would
/// have produced), or a construct silently IGNORED. Both are asserted as loud failures, not as
/// tolerances.</para>
/// </summary>
public class ProjectFileTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mw-projectfile-{Guid.NewGuid():N}");

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
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private const string MinimalProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
        </Project>
        """;

    // ── compile items ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheDefaultGlobTakesEveryCsFileAndNothingUnderBinOrObj()
    {
        var project = Write("App/App.csproj", MinimalProject);
        Write("App/One.cs", "class One;");
        Write("App/Nested/Two.cs", "class Two;");
        Write("App/bin/Release/Stale.cs", "class Stale;");
        Write("App/obj/Debug/Generated.cs", "class Generated;");
        Write("App/.hidden/Hidden.cs", "class Hidden;");

        var model = ProjectFile.Load(project);

        model.CompileItems.Select(Path.GetFileName).Order(StringComparer.Ordinal).Should().Equal("One.cs", "Two.cs");
    }

    [Fact]
    public void CompileRemoveDropsAFileTheDefaultGlobFound()
    {
        var project = Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><Compile Remove="Legacy/**/*.cs" /></ItemGroup>
            </Project>
            """);
        Write("App/Keep.cs", "class Keep;");
        Write("App/Legacy/Drop.cs", "class Drop;");
        Write("App/Legacy/Deep/AlsoDrop.cs", "class AlsoDrop;");

        var model = ProjectFile.Load(project);

        model.CompileItems.Select(Path.GetFileName).Should().Equal("Keep.cs");
    }

    [Fact]
    public void AnExplicitIncludeReachesOutsideTheProjectDirectory()
    {
        var project = Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup><Compile Include="Shared.cs" /></ItemGroup>
            </Project>
            """);
        Write("App/Shared.cs", "class Shared;");
        Write("App/NotIncluded.cs", "class NotIncluded;");

        var model = ProjectFile.Load(project);

        model.CompileItems.Select(Path.GetFileName).Should().Equal("Shared.cs");
    }

    [Fact]
    public void AProjectWithNoSourcesIsAFailureRatherThanAGreenNoOp()
    {
        var project = Write("Empty/Empty.csproj", MinimalProject);

        Action act = () => ProjectFile.Load(project);

        act.Should().Throw<ProjectFile.UnsupportedConstructException>()
            .Which.Message.Should().Contain("no source files");
    }

    // ── Directory.Build.props inheritance ──────────────────────────────────────────────────────

    [Fact]
    public void TheNearestDirectoryBuildPropsSuppliesThePropertiesTheProjectDoesNotSet()
    {
        Write("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <Nullable>enable</Nullable>
                <LangVersion>latest</LangVersion>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
                <ImplicitUsings>enable</ImplicitUsings>
                <NoWarn>$(NoWarn);CS1591;1573</NoWarn>
                <WarningsNotAsErrors>NU1901;$(WarningsNotAsErrors)</WarningsNotAsErrors>
              </PropertyGroup>
            </Project>
            """);
        var project = Write("App/App.csproj", MinimalProject);
        Write("App/One.cs", "class One;");

        var model = ProjectFile.Load(project);

        model.NullableOptions.Should().Be(NullableContextOptions.Enable);
        model.LanguageVersion.Should().Be(LanguageVersion.Latest);
        model.TreatWarningsAsErrors.Should().BeTrue();
        model.GenerateDocumentationFile.Should().BeTrue();
        // 🚨 Two things at once. The SDK's own default NoWarn (1701;1702 — the binding-redirect
        // advisories) leads, because Microsoft.NET.Sdk seeds it before any Directory.Build.props
        // appends to $(NoWarn); and a bare number is a valid MSBuild NoWarn and an INVALID Roslyn
        // diagnostic id, so it is normalised here or the suppression silently applies to nothing.
        model.NoWarn.Should().Equal("CS1701", "CS1702", "CS1591", "CS1573");
        model.WarningsNotAsErrors.Should().Equal("NU1901");
        model.GlobalUsings.Should().Contain("System.Linq");
    }

    [Fact]
    public void TheProjectWinsOverTheDirectoryBuildProps()
    {
        Write("Directory.Build.props", """
            <Project><PropertyGroup><GenerateDocumentationFile>true</GenerateDocumentationFile></PropertyGroup></Project>
            """);
        var project = Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <GenerateDocumentationFile>false</GenerateDocumentationFile>
                <AssemblyName>Renamed</AssemblyName>
                <RootNamespace>Some.Namespace</RootNamespace>
                <DefineConstants>$(DefineConstants);FEATURE_X</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        Write("App/One.cs", "class One;");

        var model = ProjectFile.Load(project);

        model.GenerateDocumentationFile.Should().BeFalse();
        model.AssemblyName.Should().Be("Renamed");
        model.RootNamespace.Should().Be("Some.Namespace");
        model.DefineConstants.Should().Contain("FEATURE_X");
    }

    [Fact]
    public void ADirectoryBuildTargetsIsImportedAfterTheProjectBody()
    {
        Write("Directory.Build.targets", """
            <Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>
            """);
        var project = Write("App/App.csproj", MinimalProject);
        Write("App/One.cs", "class One;");

        ProjectFile.Load(project).NullableOptions.Should().Be(NullableContextOptions.Enable);
    }

    [Fact]
    public void OnlyTheNearestDirectoryBuildPropsIsImported()
    {
        // MSBuild imports the nearest one and stops; a props file that wants its parent imports it
        // explicitly. Walking up and merging both would apply settings the SDK never applies.
        Write("Directory.Build.props", """
            <Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>
            """);
        Write("Inner/Directory.Build.props", """
            <Project><PropertyGroup><LangVersion>latest</LangVersion></PropertyGroup></Project>
            """);
        var project = Write("Inner/App/App.csproj", MinimalProject);
        Write("Inner/App/One.cs", "class One;");

        var model = ProjectFile.Load(project);

        model.LanguageVersion.Should().Be(LanguageVersion.Latest);
        model.NullableOptions.Should().Be(NullableContextOptions.Disable);
    }

    // ── conditions and imports ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSupportedConditionGrammarIsEvaluatedRatherThanGuessed()
    {
        Write("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <MeshWeaverRoot Condition="'$(MeshWeaverRoot)' == ''">/somewhere/else</MeshWeaverRoot>
                <Nullable Condition="'$(MeshWeaverRoot)' != ''">enable</Nullable>
                <AllowUnsafeBlocks Condition="Exists('$(MSBuildThisFileDirectory)Directory.Build.props')">true</AllowUnsafeBlocks>
                <LangVersion Condition="$(MSBuildProjectName.EndsWith('.Test'))">preview</LangVersion>
              </PropertyGroup>
              <!-- An import whose condition is false must be SKIPPED, not attempted: this is the
                   shape MeshWeaver.Plugins' src/Directory.Build.props uses for the test-only props. -->
              <Import Project="/definitely/not/here.props"
                      Condition="$(MSBuildProjectName.EndsWith('.Test')) AND Exists('/definitely/not/here.props')" />
            </Project>
            """);
        var project = Write("App/App.csproj", MinimalProject);
        Write("App/One.cs", "class One;");

        var model = ProjectFile.Load(project);

        model.Properties["MeshWeaverRoot"].Should().Be("/somewhere/else");
        model.NullableOptions.Should().Be(NullableContextOptions.Enable);
        model.AllowUnsafe.Should().BeTrue();
        model.LanguageVersion.Should().Be(LanguageVersion.Default);
    }

    [Fact]
    public void AConditionOutsideTheGrammarFailsByNameRatherThanEvaluatingToFalse()
    {
        var project = Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup Condition="'$([System.DateTime]::Now.Year)' &gt; '2000'">
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        Write("App/One.cs", "class One;");

        Action act = () => ProjectFile.Load(project);

        // Named, verbatim — the operator has to be able to see WHICH expression stopped the build.
        act.Should().Throw<ProjectFile.UnsupportedConstructException>()
            .Which.Message.Should().Contain("System.DateTime");
    }

    [Fact]
    public void AnUnconditionalImportOfAMissingFileFailsTheWayMsbuildDoes()
    {
        var project = Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Import Project="Nowhere.targets" />
            </Project>
            """);
        Write("App/One.cs", "class One;");

        Action act = () => ProjectFile.Load(project);

        act.Should().Throw<ProjectFile.UnsupportedConstructException>()
            .Which.Message.Should().Contain("MSB4019");
    }

    // ── constructs that must fail loudly ───────────────────────────────────────────────────────

    [Fact]
    public void ATargetFailsTheLoadUntilItIsAcknowledged()
    {
        var project = Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Target Name="VerifySomething" BeforeTargets="CoreCompile" />
            </Project>
            """);
        Write("App/One.cs", "class One;");

        Action act = () => ProjectFile.Load(project);
        act.Should().Throw<ProjectFile.UnsupportedConstructException>()
            .Which.Message.Should().Contain("VerifySomething");

        var accepted = ProjectFile.Load(project, ["target:VerifySomething"]);
        accepted.UnexecutedTargets.Should().ContainSingle().Which.Should().Contain("VerifySomething");
    }

    [Fact]
    public void AnUnknownElementIsNeverIgnoredInSilence()
    {
        var project = Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Choose><When Condition="true" /></Choose>
            </Project>
            """);
        Write("App/One.cs", "class One;");

        Action act = () => ProjectFile.Load(project);

        act.Should().Throw<ProjectFile.UnsupportedConstructException>()
            .Which.Message.Should().Contain("Choose");
    }

    [Fact]
    public void AnEmbeddedResourceFailsUntilAcknowledged_BecauseTheAssemblyWouldDiffer()
    {
        var project = Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><EmbeddedResource Include="Data.txt" /></ItemGroup>
            </Project>
            """);
        Write("App/One.cs", "class One;");
        Write("App/Data.txt", "payload");

        Action act = () => ProjectFile.Load(project);
        act.Should().Throw<ProjectFile.UnsupportedConstructException>()
            .Which.Message.Should().Contain("embedded-resource");

        ProjectFile.Load(project, [ProjectFile.Accept.EmbeddedResource]).CompileItems.Should().HaveCount(1);
    }

    // ── references ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProjectAndPackageReferencesAreReadWithTheirCentralVersions()
    {
        Write("Directory.Packages.props", """
            <Project>
              <ItemGroup><PackageVersion Include="Some.Library" Version="4.2.0" /></ItemGroup>
            </Project>
            """);
        var project = Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Lib/Lib.csproj" />
                <PackageReference Include="Some.Library" />
                <PackageReference Include="Pinned.Library" Version="1.2.3" />
              </ItemGroup>
            </Project>
            """);
        Write("App/One.cs", "class One;");
        Write("Lib/Lib.csproj", MinimalProject);

        var model = ProjectFile.Load(project);

        model.ProjectReferences.Should().ContainSingle()
            .Which.Should().Be(Path.GetFullPath(Path.Combine(_root, "Lib", "Lib.csproj")));
        model.PackageReferences.Should().HaveCount(2);
        model.PackageReferences.Single(p => p.Id == "Some.Library").Version.Should().Be("4.2.0");
        model.PackageReferences.Single(p => p.Id == "Pinned.Library").Version.Should().Be("1.2.3");
    }

    [Fact]
    public void ExplicitUsingItemsJoinTheImplicitOnes()
    {
        var project = Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup><Using Include="System.Collections.Immutable" /></ItemGroup>
            </Project>
            """);
        Write("App/One.cs", "class One;");

        ProjectFile.Load(project).GlobalUsings.Should().Contain("System.Collections.Immutable");
    }

    [Fact]
    public void ImplicitUsingsAreAbsentWhenTheProjectDidNotAskForThem()
    {
        var project = Write("App/App.csproj", MinimalProject);
        Write("App/One.cs", "class One;");

        ProjectFile.Load(project).GlobalUsings.Should().BeEmpty();
    }

    [Fact]
    public void TheOsPlatformFunctionIsEvaluatedAgainstTheRunningOs()
    {
        // $([MSBuild]::IsOSPlatform('OSX')) guards native-interop references in three projects
        // here. It has an exact answer, so it gets one rather than a named failure.
        var project = Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OnMac Condition="$([MSBuild]::IsOSPlatform('OSX'))">yes</OnMac>
                <OnLinux Condition="$([MSBuild]::IsOSPlatform('Linux'))">yes</OnLinux>
              </PropertyGroup>
            </Project>
            """);
        Write("App/One.cs", "class One;");

        var model = ProjectFile.Load(project);

        model.Properties.GetValueOrDefault("OnMac", "").Should().Be(OperatingSystem.IsMacOS() ? "yes" : "");
        model.Properties.GetValueOrDefault("OnLinux", "").Should().Be(OperatingSystem.IsLinux() ? "yes" : "");
    }

    // ── framework symbols ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheTargetFrameworkLadderIsDefined_SoAnIfDirectiveCompilesTheSameCode()
    {
        var symbols = ProjectFile.FrameworkSymbols("net10.0");

        symbols.Should().Contain("NET");
        symbols.Should().Contain("NET10_0");
        symbols.Should().Contain("NET8_0_OR_GREATER");
        symbols.Should().Contain("NET10_0_OR_GREATER");
        symbols.Should().Contain("NETCOREAPP");
    }
}
