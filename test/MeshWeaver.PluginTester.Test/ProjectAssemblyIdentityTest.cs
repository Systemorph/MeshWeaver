using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// 🔴 <b>The emitted assembly's IDENTITY — the one property of this builder's output that no
/// compile, no warning and no reviewer can catch when it is wrong.</b>
///
/// <para><c>build-project</c> runs no MSBuild targets, so the SDK's <c>GenerateAssemblyInfo</c>
/// never runs and Roslyn emits its own default identity: <c>0.0.0.0</c>. Measured first-hand on
/// 2026-08-30 by building <c>MeshWeaver.Plugins/src/MeshWeaver.Speech.Contract</c> through the
/// container and reading the result — <c>AssemblyVersion=0.0.0.0</c>, where that repo's
/// <c>src/Directory.Build.props</c> pins <c>3.0.0.0</c> and the whole fleet binds <c>3.0.0.0</c>.
/// The build was GREEN. The failure would have arrived in a different repo, in a different
/// process, as <c>FileNotFoundException: Could not load file or assembly '…, Version=3.0.0.0'</c>
/// — which is exactly the shape of Systemorph/MeshWeaver#143.</para>
///
/// <para><b>Every assertion here reads the emitted FILE.</b> Inspecting the synthesized source
/// would prove only that this suite and the builder agree about a string; the claim under test is
/// about the bytes on disk. The version table is not recalled either — it was
/// measured against SDK 10.0.400 by building the same projects with <c>dotnet build</c> and
/// reading the generated <c>*.AssemblyInfo.cs</c>.</para>
/// </summary>
public class ProjectAssemblyIdentityTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mw-asmidentity-{Guid.NewGuid():N}");

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
    /// An <c>/app</c>-shaped reference directory, built the same way <see cref="ProjectBuildTest"/>
    /// builds one: the suite's own real <c>.deps.json</c> plus one assembly, with everything else
    /// arriving through the process's TPA as the shared framework does in a container.
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

    private static Task<ProjectBuild.Report> Build(ProjectBuild.Options options) =>
        ProjectBuild.Run(options).Await(TestContext.Current.CancellationToken);

    /// <summary>Writes a library whose only property block is <paramref name="properties"/>.</summary>
    private string Library(string properties, string? sourceText = null)
    {
        var project = Write("Lib/Lib.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
            {properties}
              </PropertyGroup>
            </Project>
            """);
        Write("Lib/Thing.cs", sourceText ?? "namespace Lib; public class Thing;");
        return project;
    }

    /// <summary>
    /// Builds and returns the EMITTED assembly's own name — read off the file, never off the model.
    /// </summary>
    private async Task<(AssemblyName Name, string Path)> Emit(ProjectBuild.Options options)
    {
        var report = await Build(options);
        report.FatalError.Should().BeNull();
        report.ExitCode.Should().Be(0);
        var path = report.Projects.Single().Result!.AssemblyPath;
        path.Should().NotBeNull();
        return (AssemblyName.GetAssemblyName(path!), path!);
    }

    /// <summary>
    /// Every assembly-level attribute in the emitted file, read out of its METADATA.
    ///
    /// <para>🚨 Deliberately not <see cref="Assembly.LoadFrom(string)"/>. These cases differ only in
    /// the identity of an assembly whose simple name is the same in all of them, and the default
    /// load context refuses the second one — <c>Assembly with same name is already loaded</c> —
    /// which would turn an identity suite into a load-order suite. Reading the metadata also asserts
    /// the thing actually claimed: what is in the BYTES.</para>
    /// </summary>
    private static ImmutableArray<(string TypeName, ImmutableArray<string?> Arguments)> AssemblyAttributes(
        string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var found = ImmutableArray.CreateBuilder<(string, ImmutableArray<string?>)>();
        foreach (var handle in metadata.CustomAttributes)
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Parent.Kind != HandleKind.AssemblyDefinition)
                continue;
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
                continue;
            var member = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference)
                continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            var typeName = $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}";
            var value = attribute.DecodeValue(StringOnlyAttributeTypes.Instance);
            found.Add((typeName, [.. value.FixedArguments.Select(a => a.Value as string)]));
        }
        return found.ToImmutable();
    }

    /// <summary>The single-value read: the first constructor argument of one attribute type.</summary>
    private static string? AttributeValue(string assemblyPath, string attributeTypeName) =>
        AssemblyAttributes(assemblyPath)
            .Where(a => a.TypeName == attributeTypeName)
            .Select(a => a.Arguments.Length > 0 ? a.Arguments[0] : null)
            .FirstOrDefault();

    /// <summary>
    /// The minimum a <see cref="CustomAttribute.DecodeValue{TType}"/> needs. Every attribute
    /// <c>GenerateAssemblyInfo</c> writes takes string arguments only, so nothing here has to
    /// resolve a real type.
    /// </summary>
    private sealed class StringOnlyAttributeTypes : ICustomAttributeTypeProvider<string>
    {
        internal static readonly StringOnlyAttributeTypes Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSystemType() => "System.Type";

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            "definition";

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            "reference";

        public string GetTypeFromSerializedName(string name) => name;

        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

        public bool IsSystemType(string type) => type == "System.Type";
    }

    // ── the defect ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 <b>THE MUTATION PROOF.</b> Revert the synthesized assembly-info tree in
    /// <c>ProjectBuild.Compile</c> and this test fails with <c>Expected 3.0.0.0, found
    /// 0.0.0.0</c> — the measured defect, verbatim. The shape is MeshWeaver.Plugins':
    /// <c>AssemblyVersion</c> pinned as a LITERAL in a <c>Directory.Build.props</c> that the
    /// project itself never mentions.
    /// </summary>
    [Fact]
    public async Task ADirectoryBuildPropsAssemblyVersionIsWhatTheEMITTEDAssemblyCarries()
    {
        Write("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <AssemblyVersion>3.0.0.0</AssemblyVersion>
                <FileVersion>3.0.0.0</FileVersion>
              </PropertyGroup>
            </Project>
            """);
        var project = Library("    <Nullable>enable</Nullable>");

        var (name, path) = await Emit(OptionsFor(project));

        name.Version.Should().Be(new Version(3, 0, 0, 0),
            "the fleet binds 3.0.0.0 — an assembly at 0.0.0.0 fails at runtime binding in another "
            + "repo, never at build time here");
        AttributeValue(path, "System.Reflection.AssemblyFileVersionAttribute").Should().Be("3.0.0.0");
    }

    /// <summary>
    /// <c>--bind-to-image</c>: the emitted identity is the IMAGE's, whatever the project or its
    /// <c>Directory.Build.props</c> say. A module built inside a platform image loads into that
    /// image's process, and the evaluator runs no MSBuild property functions, so a repository cannot
    /// derive the value itself — MeshWeaver.Plugins tried and every module came out 1.0.0.0, and its
    /// literal 3.0.0.0 reddened every image build the day the platform line moved to 3.1.0.
    /// </summary>
    [Fact]
    public async Task BindToImageStampsTheImagesOwnIdentityOverThePropsLiteral()
    {
        Write("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <AssemblyVersion>9.9.9.9</AssemblyVersion>
                <FileVersion>9.9.9.9</FileVersion>
              </PropertyGroup>
            </Project>
            """);
        var project = Library("    <Nullable>enable</Nullable>");
        var image = AssemblyName.GetAssemblyName(Path.Combine(AppDirectory(), "MeshWeaver.ShortGuid.dll")).Version!;
        image.Should().NotBe(new Version(9, 9, 9, 9), "the fixture must be able to tell the two apart");

        var (name, path) = await Emit(OptionsFor(project) with { BindToImage = true });

        name.Version.Should().Be(image,
            "the image is the process the module loads into; its identity is the only one that binds");
        AttributeValue(path, "System.Reflection.AssemblyFileVersionAttribute").Should().Be(image.ToString());
    }

    /// <summary>
    /// A caller's explicit global still wins under <c>--bind-to-image</c>: a global that silently
    /// changed value would be the defect globals exist to prevent.
    /// </summary>
    [Fact]
    public async Task BindToImageYieldsToAnExplicitGlobalProperty()
    {
        var project = Library("    <Nullable>enable</Nullable>");

        var (name, _) = await Emit(OptionsFor(project) with
        {
            BindToImage = true,
            Properties = new Dictionary<string, string> { ["AssemblyVersion"] = "2.0.0.0" },
        });

        name.Version.Should().Be(new Version(2, 0, 0, 0));
    }

    /// <summary>Without the flag the evaluator keeps its SDK-parity semantics: the props literal wins.</summary>
    [Fact]
    public async Task WithoutBindToImageThePropsLiteralStillWins()
    {
        Write("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <AssemblyVersion>9.9.9.9</AssemblyVersion>
              </PropertyGroup>
            </Project>
            """);
        var project = Library("    <Nullable>enable</Nullable>");

        var (name, _) = await Emit(OptionsFor(project));

        name.Version.Should().Be(new Version(9, 9, 9, 9));
    }

    // ── the SDK's derivation rules, as measured against SDK 10.0.400 ───────────────────────────

    /// <summary>
    /// <c>GetAssemblyVersion</c>: with no explicit <c>AssemblyVersion</c>, the binding identity is
    /// the NUMERIC core of <c>$(Version)</c>, normalised to four fields. Each row was produced by
    /// building the same project with the real SDK and reading its generated AssemblyInfo.
    /// </summary>
    [Theory]
    [InlineData("1.2.3-beta.4", "1.2.3.0")]   // the pre-release label is dropped
    [InlineData("1.2.3+meta", "1.2.3.0")]     // …so is build metadata
    [InlineData("1.2.3.4", "1.2.3.4")]        // four fields survive intact
    [InlineData("1.2.3", "1.2.3.0")]          // three are padded
    [InlineData("4.5", "4.5.0.0")]            // two are padded
    [InlineData("1", "1.0.0.0")]              // one is padded
    [InlineData("01.2.3", "1.2.3.0")]         // leading zeros normalise
    public async Task TheVersionPropertyDerivesTheBindingIdentityTheWayTheSdkDoes(
        string version, string expected)
    {
        var project = Library($"    <Version>{version}</Version>");

        var (name, _) = await Emit(OptionsFor(project));

        name.Version.Should().Be(Version.Parse(expected));
    }

    /// <summary>
    /// No version anywhere is the SDK's <c>VersionPrefix</c> default — <c>1.0.0</c> — and the
    /// resulting binding identity is <c>1.0.0.0</c>, NOT Roslyn's <c>0.0.0.0</c>. The two look
    /// alike in a log and are a different assembly to the loader.
    /// </summary>
    [Fact]
    public async Task WithNoVersionAtAllTheIdentityIsTheSdksDefaultAndNotRoslynsDefault()
    {
        var project = Library("    <Nullable>enable</Nullable>");

        var (name, path) = await Emit(OptionsFor(project));

        name.Version.Should().Be(new Version(1, 0, 0, 0));
        AttributeValue(path, "System.Reflection.AssemblyInformationalVersionAttribute").Should().Be("1.0.0");
    }

    /// <summary><c>VersionPrefix</c> + <c>VersionSuffix</c> compose <c>$(Version)</c>.</summary>
    [Fact]
    public async Task VersionPrefixAndSuffixComposeTheVersionTheIdentityDerivesFrom()
    {
        var project = Library("""
                <VersionPrefix>2.5.0</VersionPrefix>
                <VersionSuffix>rc1</VersionSuffix>
            """);

        var (name, path) = await Emit(OptionsFor(project));

        name.Version.Should().Be(new Version(2, 5, 0, 0));
        AttributeValue(path, "System.Reflection.AssemblyInformationalVersionAttribute").Should().Be("2.5.0-rc1");
    }

    /// <summary>
    /// The fallback ORDER, which is the part that is easy to get backwards: <c>FileVersion</c>
    /// follows <c>AssemblyVersion</c> — including an EXPLICIT one — and never <c>$(Version)</c>,
    /// while <c>InformationalVersion</c> follows <c>$(Version)</c>.
    /// </summary>
    [Fact]
    public async Task FileVersionFollowsAssemblyVersionAndInformationalVersionFollowsVersion()
    {
        var project = Library("""
                <Version>7.8.9</Version>
                <AssemblyVersion>3.0.0.0</AssemblyVersion>
            """);

        var (name, path) = await Emit(OptionsFor(project));

        name.Version.Should().Be(new Version(3, 0, 0, 0));
        AttributeValue(path, "System.Reflection.AssemblyFileVersionAttribute").Should().Be("3.0.0.0");
        AttributeValue(path, "System.Reflection.AssemblyInformationalVersionAttribute").Should().Be("7.8.9");
    }

    /// <summary>
    /// A <c>$(Version)</c> the SDK's task would reject is a NAMED refusal, never a fallback. A
    /// plausible-looking substitute here is the whole defect: it cannot be caught downstream.
    /// </summary>
    [Fact]
    public void AnUnparseableVersionIsARefusalThatNamesTheProperty()
    {
        var project = Library("    <Version>1.2.3.4.5</Version>");

        Action act = () => ProjectFile.Load(project);

        act.Should().Throw<ProjectFile.UnsupportedConstructException>()
            .Which.Message.Should().Contain("Version='1.2.3.4.5'");
    }

    // ── GenerateAssemblyInfo=false: the project supplies its own ───────────────────────────────

    /// <summary>
    /// <c>GenerateAssemblyInfo=false</c> means the project's own source carries the attributes.
    /// Synthesizing a second set would be CS0579, so nothing is synthesized — and the identity the
    /// SOURCE declares is the one that reaches the assembly.
    /// </summary>
    [Fact]
    public async Task GenerateAssemblyInfoFalseEmitsNothingAndTheProjectsOwnAttributesWin()
    {
        var project = Library(
            "    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>\n    <Version>9.9.9</Version>",
            """
            [assembly: System.Reflection.AssemblyVersion("2.1.0.0")]
            [assembly: System.Reflection.AssemblyFileVersion("2.1.0.0")]
            namespace Lib; public class Thing;
            """);

        var (name, path) = await Emit(OptionsFor(project));

        name.Version.Should().Be(new Version(2, 1, 0, 0));
        AttributeValue(path, "System.Reflection.AssemblyFileVersionAttribute").Should().Be("2.1.0.0");
    }

    /// <summary>
    /// …and the CONTROL for the case above: leave <c>GenerateAssemblyInfo</c> at its default and
    /// the same source is the SDK's own duplicate-attribute error, CS0579. Without this the test
    /// above would pass just as well against a builder that synthesizes nothing at all.
    /// </summary>
    [Fact]
    public async Task WithoutThatFlagTheSameSourceIsCS0579JustAsItIsUnderTheSdk()
    {
        var project = Library("    <Version>9.9.9</Version>", """
            [assembly: System.Reflection.AssemblyVersion("2.1.0.0")]
            namespace Lib; public class Thing;
            """);

        var report = await Build(OptionsFor(project));

        report.ExitCode.Should().Be(1);
        report.Activity.Messages.Select(m => m.Message)
            .Should().Contain(m => m.Contains("CS0579", StringComparison.Ordinal));
    }

    // ── the rest of what GenerateAssemblyInfo writes ───────────────────────────────────────────

    /// <summary>
    /// <c>Description</c> is set on nearly every project in these repos and was silently absent
    /// from everything this builder emitted. So were <c>Company</c>, <c>Product</c> and
    /// <c>Title</c>, whose defaults come off <c>$(AssemblyName)</c> through <c>$(Authors)</c>.
    /// </summary>
    [Fact]
    public async Task TheDescriptiveAttributesAreEmittedWithTheSdksOwnFallbackChain()
    {
        var project = Library("""
                <Description>A seam under test.</Description>
                <Authors>Systemorph</Authors>
            """);

        var (_, path) = await Emit(OptionsFor(project));

        AttributeValue(path, "System.Reflection.AssemblyDescriptionAttribute").Should().Be("A seam under test.");
        AttributeValue(path, "System.Reflection.AssemblyCompanyAttribute").Should().Be("Systemorph");
        AttributeValue(path, "System.Reflection.AssemblyProductAttribute").Should().Be("Lib");
        AttributeValue(path, "System.Reflection.AssemblyTitleAttribute").Should().Be("Lib");
        AttributeValue(path, "System.Reflection.AssemblyConfigurationAttribute").Should().Be("Release");
    }

    /// <summary>
    /// An individual <c>Generate…Attribute</c> switch turns one attribute off, exactly as under the
    /// SDK — the mechanism a project uses when it declares that attribute itself.
    /// </summary>
    [Fact]
    public async Task AGenerateAttributeSwitchSuppressesJustThatOneAttribute()
    {
        var project = Library("""
                <Description>Present.</Description>
                <GenerateAssemblyDescriptionAttribute>false</GenerateAssemblyDescriptionAttribute>
            """);

        var (name, path) = await Emit(OptionsFor(project));

        AttributeValue(path, "System.Reflection.AssemblyDescriptionAttribute").Should().BeNull();
        name.Version.Should().Be(new Version(1, 0, 0, 0), "the other attributes are untouched");
    }

    /// <summary>
    /// <c>InternalsVisibleTo</c> items — which sixty-odd projects across these repos declare — were
    /// on this evaluator's "changes nothing" list. That was true of the COMPILE and false of the
    /// ASSEMBLY: dropped here, the friend project fails to compile in a later, unrelated run.
    /// </summary>
    [Fact]
    public async Task InternalsVisibleToItemsBecomeAttributesOnTheEmittedAssembly()
    {
        var project = Write("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <InternalsVisibleTo Include="Lib.Test" />
                <AssemblyMetadata Include="CommitHash" Value="deadbeef" />
              </ItemGroup>
            </Project>
            """);
        Write("Lib/Thing.cs", "namespace Lib; public class Thing;");

        var (_, path) = await Emit(OptionsFor(project));

        var attributes = AssemblyAttributes(path);
        attributes
            .Where(a => a.TypeName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute")
            .Select(a => a.Arguments[0])
            .Should().Equal("Lib.Test");
        attributes
            .Where(a => a.TypeName == "System.Reflection.AssemblyMetadataAttribute")
            .Select(a => (a.Arguments[0], a.Arguments[1]))
            .Should().Contain(("CommitHash", "deadbeef"));
    }

    /// <summary>
    /// A raw <c>&lt;AssemblyAttribute&gt;</c> item — how the platform's own
    /// <c>Directory.Build.props</c> stamps its commit hash — carries its <c>_ParameterN</c>
    /// metadata through in order.
    /// </summary>
    [Fact]
    public async Task AnAssemblyAttributeItemCarriesItsParametersThrough()
    {
        var project = Write("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
                  <_Parameter1>MeshWeaverFrameworkIdentity</_Parameter1>
                  <_Parameter2>gabc123</_Parameter2>
                </AssemblyAttribute>
              </ItemGroup>
            </Project>
            """);
        Write("Lib/Thing.cs", "namespace Lib; public class Thing;");

        var (_, path) = await Emit(OptionsFor(project));

        AssemblyAttributes(path)
            .Where(a => a.TypeName == "System.Reflection.AssemblyMetadataAttribute")
            .Select(a => (a.Arguments[0], a.Arguments[1]))
            .Should().Contain(("MeshWeaverFrameworkIdentity", "gabc123"));
    }

    /// <summary>
    /// A value carrying a quote, a backslash and a newline has to survive as ONE literal — these
    /// repos' <c>Description</c> properties carry all three, and naive concatenation would either
    /// fail to parse or, worse, parse into something else.
    /// </summary>
    [Fact]
    public async Task AnAwkwardStringSurvivesAsExactlyItself()
    {
        var project = Library("""
                <Description>He said "hi" \ then stopped.</Description>
            """);

        var (_, path) = await Emit(OptionsFor(project));

        AttributeValue(path, "System.Reflection.AssemblyDescriptionAttribute")
            .Should().Be("He said \"hi\" \\ then stopped.");
    }

    // ── SourceRevisionId: the one semantic this builder cannot compute for itself ──────────────

    /// <summary>
    /// The SDK's <c>AddSourceRevisionToInformationalVersion</c> appends the git commit under
    /// SemVer 2.0 rules. This builder runs no git, so the id arrives as a property — and when it
    /// does, the append is the SDK's, including the '.' rather than '+' when the string already
    /// carries build metadata.
    /// </summary>
    [Theory]
    [InlineData("1.2.3", "1.2.3+abc123")]
    [InlineData("1.2.3+meta", "1.2.3+meta.abc123")]
    public async Task ASuppliedSourceRevisionIdIsAppendedTheWaySemVerRequires(
        string version, string expected)
    {
        var project = Library($"    <Version>{version}</Version>");
        var options = OptionsFor(project) with
        {
            Properties = new Dictionary<string, string> { ["SourceRevisionId"] = "abc123" },
        };

        var (_, path) = await Emit(options);

        AttributeValue(path, "System.Reflection.AssemblyInformationalVersionAttribute").Should().Be(expected);
    }

    /// <summary>
    /// …and with none supplied the divergence is SAID rather than hidden: the log names the
    /// property, because an InformationalVersion quietly missing its <c>+&lt;sha&gt;</c> suffix is
    /// exactly the plausible-looking difference this file exists to refuse.
    /// </summary>
    [Fact]
    public async Task WithNoSourceRevisionIdTheAbsentSuffixIsNamedInTheLog()
    {
        var project = Library("    <Version>1.2.3</Version>");

        var report = await Build(OptionsFor(project));

        report.ExitCode.Should().Be(0);
        report.Activity.Messages.Select(m => m.Message)
            .Should().Contain(m => m.Contains("SourceRevisionId", StringComparison.Ordinal));
    }

    /// <summary>
    /// A global property beats the project file, exactly as MSBuild's <c>-p:</c> does — the escape
    /// hatch a pack lane uses to stamp the platform's version onto a module.
    /// </summary>
    [Fact]
    public async Task AGlobalPropertyOverridesTheProjectsOwnAssemblyVersion()
    {
        var project = Library("    <AssemblyVersion>1.1.1.1</AssemblyVersion>");
        var options = OptionsFor(project) with
        {
            Properties = new Dictionary<string, string> { ["AssemblyVersion"] = "3.0.0.0" },
        };

        var (name, _) = await Emit(options);

        name.Version.Should().Be(new Version(3, 0, 0, 0));
    }
}
