using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MeshWeaver.Data;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// <c>&lt;EmbeddedResource&gt;</c> support for <c>mw-plugin-test build-project</c>, held to the one
/// standard that matters: <b>the manifest NAME the SDK would have produced</b>.
///
/// <para>🚨 <b>Every assertion here reads the emitted PE, never this repo's own source.</b> A
/// manifest name cannot be checked by inspecting the code that computes it — that only proves the
/// code does what it does. The failure being defended against is a name that is plausible and
/// wrong, which compiles green, ships, and returns <c>null</c> from
/// <c>GetManifestResourceStream</c> in another process. So the names come out of
/// <see cref="MetadataReader.ManifestResources"/> of the assembly this builder actually emitted, and
/// <see cref="ParityWithTheRealSdk"/> compares that set against the set a REAL
/// <c>dotnet build</c> of the same project produces.</para>
/// </summary>
public class EmbeddedResourceTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mw-embres-{Guid.NewGuid():N}");

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
    /// An <c>/app</c>-shaped reference directory, the same one <c>ProjectBuildTest</c> builds and
    /// for the same reason: a test bin carries three <c>*.deps.json</c> and the reference set
    /// correctly refuses an ambiguous one.
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
            EntryProject = entry,
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "out"),
            Output = TextWriter.Null,
            Accept = accept,
            MaxParallel = 4,
        };

    private static Task<ProjectBuild.Report> Build(ProjectBuild.Options options) =>
        ProjectBuild.Run(options).Await(TestContext.Current.CancellationToken);

    /// <summary>
    /// The manifest resource table of an emitted assembly — names and the public/private flag —
    /// read straight out of the PE. This is the only oracle in this file.
    /// </summary>
    /// <param name="assemblyPath">The DLL to read.</param>
    /// <returns>Name → attributes, in name order.</returns>
    internal static IReadOnlyDictionary<string, ManifestResourceAttributes> ResourcesIn(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.ManifestResources
            .Select(metadata.GetManifestResource)
            .ToDictionary(r => metadata.GetString(r.Name), r => r.Attributes, StringComparer.Ordinal);
    }

    /// <summary>The manifest names of an emitted assembly, ordinal-sorted so the comparison is
    /// order-free without being set-shaped (a duplicate would still show).</summary>
    /// <param name="assemblyPath">The DLL to read.</param>
    /// <returns>The names, sorted.</returns>
    internal static IReadOnlyList<string> NamesIn(string assemblyPath) =>
        [.. ResourcesIn(assemblyPath).Keys.Order(StringComparer.Ordinal)];

    /// <summary>The same ordering applied to an expectation, so a test can list names readably.</summary>
    /// <param name="names">The expected names, in any order.</param>
    /// <returns>The names, sorted.</returns>
    private static IEnumerable<string> Sorted(params string[] names) => names.Order(StringComparer.Ordinal);

    private const string Library = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <RootNamespace>Probe.Root.Ns</RootNamespace>
            <EnableDefaultEmbeddedResourceItems>false</EnableDefaultEmbeddedResourceItems>
          </PropertyGroup>
          <ItemGroup>
        {ITEMS}
          </ItemGroup>
        </Project>
        """;

    /// <summary>Writes a project whose only variable is its EmbeddedResource ItemGroup.</summary>
    private string ProjectWith(string items, string projectName = "Lib")
    {
        var path = Write($"{projectName}/{projectName}.csproj", Library.Replace("{ITEMS}", items));
        Write($"{projectName}/Type.cs", """
            namespace Probe.Root.Ns;
            /// <summary>A type, so the compile has something to do.</summary>
            public static class Type1
            {
                /// <summary>One.</summary>
                /// <returns>1.</returns>
                public static int One() => 1;
            }
            """);
        return path;
    }

    // ── the default rule, measured against the SDK ─────────────────────────────────────────────

    /// <summary>
    /// <c>$(RootNamespace).&lt;directory with separators as dots&gt;.&lt;file name&gt;</c> — and the
    /// two halves are NOT treated alike. The directory is mangled into identifiers; the file name is
    /// carried verbatim, hyphens, extra dots and all. Getting that backwards produces a name that
    /// reads perfectly well and is wrong.
    /// </summary>
    [Fact]
    public async Task TheDefaultNameIsRootNamespacePlusTheMangledDirectoryPlusTheVerbatimFileName()
    {
        var project = ProjectWith("""
                <EmbeddedResource Include="Top.md" />
                <EmbeddedResource Include="Data\**\*.md" />
                <EmbeddedResource Include="Weird-File.Name.md" />
            """);
        Write("Lib/Top.md", "t");
        Write("Lib/Weird-File.Name.md", "w");
        Write("Lib/Data/One.md", "1");
        Write("Lib/Data/Nested/Two.md", "2");
        Write("Lib/Data/with-dash/Three.md", "3");
        Write("Lib/Data/9digits/Four.md", "4");
        Write("Lib/Data/space dir/Five.md", "5");
        Write("Lib/Data/Dot.Dir/Six.md", "6");

        var report = await Build(OptionsFor(project));
        report.ExitCode.Should().Be(0);

        // Measured with the real SDK (probe p1). Every one of these was READ OUT of an assembly
        // `dotnet build` produced, not derived from a rule anyone remembered.
        NamesIn(report.Projects.Single().Result!.AssemblyPath!)
            .Should().Equal(Sorted(
            [
                "Probe.Root.Ns.Top.md",
                "Probe.Root.Ns.Weird-File.Name.md",      // the FILE name keeps its hyphen and dot
                "Probe.Root.Ns.Data.One.md",
                "Probe.Root.Ns.Data.Nested.Two.md",
                "Probe.Root.Ns.Data.with_dash.Three.md", // the DIRECTORY does not
                "Probe.Root.Ns.Data._9digits.Four.md",   // a leading digit is PREFIXED, not replaced
                "Probe.Root.Ns.Data.space_dir.Five.md",
                "Probe.Root.Ns.Data.Dot.Dir.Six.md",     // a dot in a directory stays a dot
            ]));
    }

    /// <summary>
    /// The bytes have to arrive too, and under the name — a resource table entry pointing at the
    /// wrong offset is exactly as invisible as a wrong name.
    /// </summary>
    [Fact]
    public async Task TheEmbEDDEDBytesAreTheFilesBytesAndTheResourceIsPUBLIC()
    {
        // 🚨 A unique project name, because this is the one test here that LOADS the emitted
        // assembly: the default AssemblyLoadContext dedupes by simple name, so a second `Lib` in the
        // same test process fails with "Assembly with same name is already loaded" — a collision
        // with ProjectBuildTest, not a defect in either.
        var project = ProjectWith("""    <EmbeddedResource Include="Data\payload.txt" />""", "PayloadLib");
        Write("PayloadLib/Data/payload.txt", "the exact bytes");

        var report = await Build(OptionsFor(project));
        report.ExitCode.Should().Be(0);
        var assemblyPath = report.Projects.Single().Result!.AssemblyPath!;

        // 🚨 Public, not private. Every resource the SDK emits carries
        // ManifestResourceAttributes.Public (measured); a private one is invisible to
        // GetManifestResourceStream from outside the assembly — the same silent null again.
        ResourcesIn(assemblyPath)["Probe.Root.Ns.Data.payload.txt"]
            .Should().Be(ManifestResourceAttributes.Public);

        var assembly = Assembly.LoadFrom(assemblyPath);
        using var stream = assembly.GetManifestResourceStream("Probe.Root.Ns.Data.payload.txt");
        stream.Should().NotBeNull("the name in the table is the name the loader answers to");
        new StreamReader(stream!).ReadToEnd().Should().Be("the exact bytes");
    }

    /// <summary>
    /// <c>$(RootNamespace)</c> defaults to the PROJECT NAME, never to <c>$(AssemblyName)</c> —
    /// measured with a project whose two differ. This was wrong in the evaluator while
    /// <c>RootNamespace</c> was informational, and it prefixes every resource name now.
    /// </summary>
    [Fact]
    public async Task RootNamespaceDefaultsToTheProjectNameNotTheAssemblyName()
    {
        var project = Write("ProjNameDiffers/ProjNameDiffers.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>DifferentAsmName</AssemblyName>
                <EnableDefaultEmbeddedResourceItems>false</EnableDefaultEmbeddedResourceItems>
              </PropertyGroup>
              <ItemGroup><EmbeddedResource Include="A.md" /></ItemGroup>
            </Project>
            """);
        Write("ProjNameDiffers/A.md", "a");
        Write("ProjNameDiffers/T.cs", "namespace P; internal static class T { }");

        var report = await Build(OptionsFor(project));
        report.ExitCode.Should().Be(0);
        NamesIn(report.Projects.Single().Result!.AssemblyPath!)
            .Should().Equal(Sorted(["ProjNameDiffers.A.md"]));
    }

    /// <summary>An empty <c>RootNamespace</c> means no prefix at all — not the assembly name.</summary>
    [Fact]
    public async Task AnEmptyRootNamespaceMeansNoPrefix()
    {
        var project = Write("Bare/Bare.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <RootNamespace></RootNamespace>
                <EnableDefaultEmbeddedResourceItems>false</EnableDefaultEmbeddedResourceItems>
              </PropertyGroup>
              <ItemGroup><EmbeddedResource Include="d\In.md" /></ItemGroup>
            </Project>
            """);
        Write("Bare/d/In.md", "i");
        Write("Bare/T.cs", "namespace P; internal static class T { }");

        var report = await Build(OptionsFor(project));
        report.ExitCode.Should().Be(0);
        NamesIn(report.Projects.Single().Result!.AssemblyPath!)
            .Should().Equal(Sorted(["d.In.md"]));
    }

    // ── the metadata that changes the name ─────────────────────────────────────────────────────

    /// <summary><c>LogicalName</c> replaces the computed name outright — in either XML form.</summary>
    [Fact]
    public async Task LogicalNameOverridesEverything_AsAnAttributeAndAsAChildElement()
    {
        var project = ProjectWith("""
                <EmbeddedResource Include="A.md" LogicalName="From.The.Attribute" />
                <EmbeddedResource Include="B.md">
                  <LogicalName>From.The.Child.Element</LogicalName>
                </EmbeddedResource>
            """);
        Write("Lib/A.md", "a");
        Write("Lib/B.md", "b");

        var report = await Build(OptionsFor(project));
        report.ExitCode.Should().Be(0);
        NamesIn(report.Projects.Single().Result!.AssemblyPath!)
            .Should().Equal(Sorted(["From.The.Attribute", "From.The.Child.Element"]));
    }

    /// <summary>
    /// <c>Link</c> replaces the PATH the name is computed from — even for a file that is inside the
    /// project — and <c>TargetPath</c> beats <c>Link</c>. Both measured.
    /// </summary>
    [Fact]
    public async Task LinkRenamesThePathAndTargetPathBeatsLink()
    {
        var project = ProjectWith("""
                <EmbeddedResource Include="inner\F.md" Link="re\named\G.md" />
                <EmbeddedResource Include="inner\H.md" TargetPath="hand\set\P.md" Link="ignored\me.md" />
            """);
        Write("Lib/inner/F.md", "f");
        Write("Lib/inner/H.md", "h");

        var report = await Build(OptionsFor(project));
        report.ExitCode.Should().Be(0);
        NamesIn(report.Projects.Single().Result!.AssemblyPath!)
            .Should().Equal(Sorted(["Probe.Root.Ns.re.named.G.md", "Probe.Root.Ns.hand.set.P.md"]));
    }

    /// <summary>
    /// A file OUTSIDE the project with no <c>Link</c> loses its directory ENTIRELY — the target path
    /// falls all the way back to the bare file name. Nothing about
    /// <c>Include="..\shared\Shared.md"</c> suggests the resource will be called
    /// <c>&lt;ns&gt;.Shared.md</c>, which is precisely why it is measured rather than assumed.
    /// </summary>
    [Fact]
    public async Task AFileOutsideTheProjectLosesItsDirectoryRatherThanKeepingDotDot()
    {
        var project = ProjectWith("""    <EmbeddedResource Include="..\shared\Shared.md" />""");
        Write("shared/Shared.md", "s");

        var report = await Build(OptionsFor(project));
        report.ExitCode.Should().Be(0);
        NamesIn(report.Projects.Single().Result!.AssemblyPath!)
            .Should().Equal(Sorted(["Probe.Root.Ns.Shared.md"]));
    }

    /// <summary><c>Exclude</c>, <c>Remove</c> and <c>Update</c> all behave as MSBuild's items do.</summary>
    [Fact]
    public async Task ExcludeAndRemoveAndUpdateAllApply()
    {
        var project = ProjectWith("""
                <EmbeddedResource Include="g\*.md" Exclude="g\Skip.md" />
                <EmbeddedResource Remove="g\Gone.md" />
                <EmbeddedResource Update="g\Kept.md" LogicalName="Updated.Name" />
            """);
        Write("Lib/g/Kept.md", "k");
        Write("Lib/g/Skip.md", "s");
        Write("Lib/g/Gone.md", "g");
        Write("Lib/g/Plain.md", "p");

        var report = await Build(OptionsFor(project));
        report.ExitCode.Should().Be(0);
        NamesIn(report.Projects.Single().Result!.AssemblyPath!)
            .Should().Equal(Sorted(["Updated.Name", "Probe.Root.Ns.g.Plain.md"]));
    }

    // ── the loud refusals ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 A culture in the file name sends the resource to a SATELLITE assembly, and an explicit
    /// <c>LogicalName</c> does NOT rescue it (measured: <c>strings.de.json</c> with a pinned
    /// LogicalName still landed in <c>de/…resources.dll</c>). This builder emits one assembly, so it
    /// refuses by name rather than embedding a resource the SDK would not have embedded.
    /// </summary>
    [Fact]
    public void ACultureInTheFileNameIsRefusedByName()
    {
        var project = ProjectWith("""
                <EmbeddedResource Include="L\strings.de.json" LogicalName="Pinned.strings.de.json" />
            """);
        Write("Lib/L/strings.de.json", "{}");

        var refusal = Assert.Throws<ProjectFile.UnsupportedConstructException>(() => ProjectFile.Load(project));
        refusal.Message.Should().Contain("strings.de.json").And.Contain("SATELLITE")
            .And.Contain("WithCulture").And.Contain(ProjectFile.Accept.CultureResource);
    }

    /// <summary>
    /// …and <c>WithCulture="false"</c> — which is what core's <c>MeshWeaver.Messaging.Hub</c>
    /// already writes — keeps it, under the name including the culture segment.
    /// </summary>
    [Fact]
    public async Task WithCultureFalseKeepsTheResourceAndItsFullFileName()
    {
        var project = ProjectWith("""
                <EmbeddedResource Include="L\strings.de.json" WithCulture="false" />
                <EmbeddedResource Include="L\strings.en.json" WithCulture="false"
                                  LogicalName="MeshWeaver.Messaging.Localization.strings.en.json" />
            """);
        Write("Lib/L/strings.de.json", "{}");
        Write("Lib/L/strings.en.json", "{}");

        var report = await Build(OptionsFor(project));
        report.ExitCode.Should().Be(0);
        NamesIn(report.Projects.Single().Result!.AssemblyPath!)
            .Should().Equal(Sorted(
            [
                "Probe.Root.Ns.L.strings.de.json",
                "MeshWeaver.Messaging.Localization.strings.en.json",
            ]));
    }

    /// <summary>A culture-SHAPED name that is not a culture is embedded normally.</summary>
    [Fact]
    public async Task ASecondExtensionThatIsNotACultureIsNotTreatedAsOne()
    {
        var project = ProjectWith("""    <EmbeddedResource Include="C\*.md" />""");
        Write("Lib/C/Foo.zz.md", "z");
        Write("Lib/C/Foo.notaculture.md", "n");

        var report = await Build(OptionsFor(project));
        report.ExitCode.Should().Be(0);
        NamesIn(report.Projects.Single().Result!.AssemblyPath!)
            .Should().Equal(Sorted(
                ["Probe.Root.Ns.C.Foo.zz.md", "Probe.Root.Ns.C.Foo.notaculture.md"]));
    }

    /// <summary>
    /// <c>.resx</c> is refused because its CONTENT needs resgen, not because of its name — and the
    /// refusal says so, including the accept token that builds without it.
    /// </summary>
    [Fact]
    public void AResxIsRefusedByNameBecauseItsContentNeedsResgen()
    {
        var project = ProjectWith("""    <EmbeddedResource Include="Res\Strings.resx" />""");
        Write("Lib/Res/Strings.resx", "<root />");

        var refusal = Assert.Throws<ProjectFile.UnsupportedConstructException>(() => ProjectFile.Load(project));
        refusal.Message.Should().Contain("Strings.resx").And.Contain("resgen")
            .And.Contain(ProjectFile.Accept.ResxResource);

        // …and accepting it SKIPS the resource, loudly, rather than embedding the XML.
        var model = ProjectFile.Load(project, [ProjectFile.Accept.ResxResource]);
        model.EmbeddedResources.Should().BeEmpty();
        model.SkippedResources.Should().ContainSingle().Which.Should().Contain("Strings.resx");
    }

    /// <summary>
    /// 🚨 The SDK's DEFAULT EmbeddedResource glob is <c>**/*.resx</c>, so a stray <c>.resx</c> nobody
    /// declared is still an SDK resource. Reproducing the default glob is what makes that a named
    /// refusal instead of an assembly quietly missing a resource the SDK would have embedded.
    /// </summary>
    [Fact]
    public void TheSdkDefaultResxGlobIsReproducedSoAStrayResxIsNotSilentlyDropped()
    {
        var project = Write("Def/Def.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        Write("Def/T.cs", "namespace P; internal static class T { }");
        Write("Def/Nested/Stray.resx", "<root />");

        Assert.Throws<ProjectFile.UnsupportedConstructException>(() => ProjectFile.Load(project))
            .Message.Should().Contain("Stray.resx");
    }

    /// <summary><c>DependentUpon</c> replaces the name with a class name; refused by name.</summary>
    [Fact]
    public void DependentUponIsRefusedByName()
    {
        var project = ProjectWith("""
                <EmbeddedResource Include="A.md" DependentUpon="Type.cs" />
            """);
        Write("Lib/A.md", "a");

        Assert.Throws<ProjectFile.UnsupportedConstructException>(() => ProjectFile.Load(project))
            .Message.Should().Contain("DependentUpon").And.Contain("first class")
            .And.Contain(ProjectFile.Accept.DependentUponResource);
    }

    /// <summary>
    /// <c>ManifestResourceName</c> metadata is refused because in the SDK it does not do what it
    /// says: it makes the naming task skip the item, so csc gets no logical name and falls back to
    /// the bare file name (measured). Reproducing that quirk would not be fidelity.
    /// </summary>
    [Fact]
    public void ManifestResourceNameMetadataIsRefusedRatherThanReproducedAsAQuirk()
    {
        var project = ProjectWith("""
                <EmbeddedResource Include="A.md" ManifestResourceName="I.Win.Outright" />
            """);
        Write("Lib/A.md", "a");

        Assert.Throws<ProjectFile.UnsupportedConstructException>(() => ProjectFile.Load(project))
            .Message.Should().Contain("ManifestResourceName").And.Contain("LogicalName");
    }

    /// <summary>
    /// A LITERAL include of a file that is not there is csc's CS1566, which fails the SDK build — so
    /// it fails here. A GLOB that matches nothing is legal and matches nothing. Both measured.
    /// </summary>
    [Fact]
    public void AMissingLiteralIncludeFailsWhileAnEmptyGlobDoesNot()
    {
        var missing = ProjectWith("""    <EmbeddedResource Include="does\not\exist.md" />""", "Missing");
        Assert.Throws<ProjectFile.UnsupportedConstructException>(() => ProjectFile.Load(missing))
            .Message.Should().Contain("does not exist").And.Contain("CS1566");

        var empty = ProjectWith("""    <EmbeddedResource Include="none\**\*.md" />""", "Empty");
        ProjectFile.Load(empty).EmbeddedResources.Should().BeEmpty();
    }

    /// <summary>
    /// Two resources that mangle to the SAME manifest name is csc's CS1508. Naming both files beats
    /// naming the collision: sibling directories <c>--</c> and <c>_</c> both mangle to <c>__</c>,
    /// and nothing about either name says so.
    /// </summary>
    [Fact]
    public void TwoResourcesClaimingOneNameAreRefusedNamingBOTHFiles()
    {
        var project = ProjectWith("""    <EmbeddedResource Include="**\F.md" />""");
        Write("Lib/--/F.md", "1");
        Write("Lib/_/F.md", "2");

        var refusal = Assert.Throws<ProjectFile.UnsupportedConstructException>(() => ProjectFile.Load(project));
        refusal.Message.Should().Contain("Probe.Root.Ns.__.F.md").And.Contain("CS1508");
        refusal.Message.Should().Contain("--").And.Contain("_");
    }

    /// <summary>
    /// The blanket escape hatch still exists and is still LOUD: <c>--accept embedded-resource</c>
    /// builds an assembly deliberately missing its resources, and says which ones.
    /// </summary>
    [Fact]
    public async Task TheBlanketAcceptSkipsEverythingAndListsWhatItSkipped()
    {
        var project = ProjectWith("""    <EmbeddedResource Include="Data\**\*.md" />""");
        Write("Lib/Data/a.md", "a");
        Write("Lib/Data/b.md", "b");

        var report = await Build(OptionsFor(project, ProjectFile.Accept.EmbeddedResource));
        report.ExitCode.Should().Be(0);
        report.Projects.Single().Result!.ResourceCount.Should().Be(0);
        ResourcesIn(report.Projects.Single().Result!.AssemblyPath!).Should().BeEmpty();
        report.Activity.Warnings().Should().Contain(m => m.Message.Contains("resource NOT embedded"));
    }

    /// <summary>
    /// 🚨 A missing BUILD OUTPUT is not a missing input, and conflating them turns a project the SDK
    /// builds GREEN into a red one. <c>MeshWeaver.Northwind.Domain</c> embeds its own XML doc as
    /// <c>bin\$(Configuration)\$(TargetFramework)\$(AssemblyName).xml</c>, and a real
    /// <c>dotnet build</c> of that shape succeeds from a CLEAN tree — measured — because csc writes
    /// <c>/doc:</c> and reads <c>/resource:</c> in one invocation. This builder writes its doc file
    /// somewhere else, so it names the case and skips it rather than failing the project.
    /// </summary>
    [Fact]
    public void EmbeddingTheBuildsOwnOutputIsNamedAndSkippable_NotAMissingInput()
    {
        var project = Write("Own/Own.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
              </PropertyGroup>
              <ItemGroup>
                <EmbeddedResource Include="bin\$(Configuration)\$(TargetFramework)\$(AssemblyName).xml">
                  <LogicalName>$(AssemblyName).xml</LogicalName>
                </EmbeddedResource>
              </ItemGroup>
            </Project>
            """);
        Write("Own/T.cs", "namespace P; internal static class T { }");

        var refusal = Assert.Throws<ProjectFile.UnsupportedConstructException>(() => ProjectFile.Load(project));
        refusal.Message.Should().Contain("build's OWN OUTPUT")
            .And.Contain(ProjectFile.Accept.BuildOutputResource);
        // 🚨 …and the property expansion has to WORK, or the item names a file called ".xml" and the
        // refusal blames the wrong thing. $(AssemblyName) and $(RootNamespace) default to the
        // PROJECT NAME, exactly as Microsoft.NET.Sdk.props sets them.
        refusal.Message.Should().Contain("Own.xml");

        var accepted = ProjectFile.Load(project, [ProjectFile.Accept.BuildOutputResource]);
        accepted.EmbeddedResources.Should().BeEmpty();
        accepted.SkippedResources.Should().ContainSingle().Which.Should().Contain("Own.xml");
    }

    // ── the parity test ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 <b>THE test.</b> One project, built twice — once by the real .NET SDK, once by
    /// <c>build-project</c> — and the two manifest resource name SETS must be identical. Everything
    /// else in this file pins a rule that was measured once; this re-measures on every run, so a
    /// future SDK that changes a rule turns THIS red rather than shipping assemblies whose resources
    /// nobody can find.
    ///
    /// <para>The project deliberately carries the hard cases together: a recursive glob, a
    /// non-identifier directory, a leading-digit directory, a dotted directory, a dotted FILE name,
    /// an explicit <c>LogicalName</c>, a <c>Link</c>, a file outside the project, and a
    /// <c>WithCulture="false"</c> file whose name carries a real culture.</para>
    ///
    /// <para>The SDK build runs with an EMPTY NuGet source list, so it needs no network: the project
    /// has no <c>PackageReference</c> and restores against the SDK's own targeting pack.</para>
    /// </summary>
    [Fact]
    public async Task ParityWithTheRealSdk()
    {
        const string items = """
                <EmbeddedResource Include="Top.md" />
                <EmbeddedResource Include="Data\**\*.md" />
                <EmbeddedResource Include="Weird-File.Name.md" />
                <EmbeddedResource Include="Logical.md" LogicalName="My.Custom.Logical.Name" />
                <EmbeddedResource Include="inner\F.md" Link="re\named\G.md" />
                <EmbeddedResource Include="..\shared\Shared.md" />
                <EmbeddedResource Include="L\strings.de.json" WithCulture="false" />
            """;
        var project = ProjectWith(items, "Parity");
        Write("Parity/Top.md", "t");
        Write("Parity/Weird-File.Name.md", "w");
        Write("Parity/Logical.md", "l");
        Write("Parity/inner/F.md", "f");
        Write("Parity/L/strings.de.json", "{}");
        Write("shared/Shared.md", "s");
        Write("Parity/Data/One.md", "1");
        Write("Parity/Data/Nested/Two.md", "2");
        Write("Parity/Data/with-dash/Three.md", "3");
        Write("Parity/Data/9digits/Four.md", "4");
        Write("Parity/Data/space dir/Five.md", "5");
        Write("Parity/Data/Dot.Dir/Six.md", "6");
        // No sources, so restore reaches nothing: the SDK build is offline and hermetic.
        Write("Parity/NuGet.config", """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration><packageSources><clear /></packageSources></configuration>
            """);

        var report = await Build(OptionsFor(project));
        report.ExitCode.Should().Be(0, "the builder under test must produce an assembly to compare");
        var ours = ResourcesIn(report.Projects.Single().Result!.AssemblyPath!).Keys.Order(StringComparer.Ordinal);

        var sdkAssembly = BuildWithTheRealSdk(Path.GetDirectoryName(project)!, "Parity");
        var theirs = ResourcesIn(sdkAssembly).Keys.Order(StringComparer.Ordinal);

        ours.Should().Equal(theirs,
            "the manifest names build-project commits to must be the ones the SDK produces — a name "
            + "that differs is a resource nothing can ever find, and it fails no other test");
    }

    /// <summary>
    /// Runs a real <c>dotnet build</c> and returns the assembly it produced.
    ///
    /// <para>🚨 It does not SKIP when the SDK is missing. A parity test that quietly passes because
    /// it could not find the thing it compares against is worse than no parity test — so an absent
    /// or failing SDK build fails this test, naming the command and its output.</para>
    /// </summary>
    private static string BuildWithTheRealSdk(string projectDirectory, string assemblyName)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = projectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "build", "-c", "Release", "--nologo", "-v", "q" })
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("could not start `dotnet` — the parity test needs the real SDK.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"the reference `dotnet build` failed in {projectDirectory} (exit {process.ExitCode}). "
                + "This test compares against the SDK's answer, so it cannot pass without one.\n" + output);

        var assembly = Path.Combine(projectDirectory, "bin", "Release", "net10.0", $"{assemblyName}.dll");
        return File.Exists(assembly)
            ? assembly
            : throw new InvalidOperationException($"the reference build produced no {assembly}.\n{output}");
    }
}
