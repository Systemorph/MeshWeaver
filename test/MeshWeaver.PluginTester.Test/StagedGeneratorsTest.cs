using System.Reactive;
using System.Reflection;
using System.Text.RegularExpressions;
using MeshWeaver.Data;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// <c>mw-plugin-test build-project</c> runs the generators the .NET SDK and the NuGet analyzer
/// packages would have run — the whole chain, EXECUTED: the image's staged copy is found beside the
/// builder, loaded against THIS repo's Roslyn, run over a real project, and the emitted assembly is
/// then LOADED and the generated code called.
///
/// <para>🚨 Every assertion exists because the failure it guards is SILENT or misattributed. A
/// missing <c>[GeneratedRegex]</c> generator looks exactly like a source file full of unimplemented
/// partial methods (a wall of CS8795). A missing ORLEANS generator looks like nothing at all — the
/// project compiles GREEN and the assembly simply has no serializers in it, which surfaces as a
/// grain activation failure in a silo, days later and nowhere near the build. So the suite proves
/// the positives by REFLECTION over the emitted assembly, and proves that each negative NAMES
/// itself.</para>
///
/// <para>This is also the gate on the SDK and Orleans pairings. Both generators are built against a
/// different Roslyn than the image carries, and both are staged from a version this repo pins; if
/// either stops running here, these tests go red on the PR that bumps it rather than in an image
/// nobody can debug.</para>
/// </summary>
public class StagedGeneratorsTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mw-stagedgen-{Guid.NewGuid():N}");

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
    /// The same <c>/app</c>-shaped fixture the other build tests use: the real shipped
    /// <c>deps.json</c> plus one assembly, with everything else arriving through this process's TPA.
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

    private ProjectBuild.Options OptionsFor(
        string entry, string? staged = null, List<LogMessage>? log = null, params string[] accept) =>
        new()
        {
            EntryProject = entry,
            AppDirectory = AppDirectory(),
            OutputDirectory = Path.Combine(_root, "out", Guid.NewGuid().ToString("N")),
            Output = TextWriter.Null,
            StagedGeneratorDirectory = staged,
            Accept = accept,
            MaxParallel = 2,
            Log = log is null ? null : Observer.Create<LogMessage>(log.Add, _ => { }, () => { }),
        };

    private static Task<ProjectBuild.Report> Build(ProjectBuild.Options options) =>
        ProjectBuild.Run(options).Await(TestContext.Current.CancellationToken);

    /// <summary>An ordinary library, exactly as the projects that use <c>[GeneratedRegex]</c> declare
    /// themselves. The assembly name is per-test because these tests LOAD what they emit.</summary>
    private static string Library(string assemblyName, string extra = "") => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <AssemblyName>{assemblyName}</AssemblyName>
            <RootNamespace>Probe</RootNamespace>
          </PropertyGroup>
          {extra}
        </Project>
        """;

    /// <summary>An evaluated model for a project that exists on disk with one source file — the
    /// evaluator refuses a project with none, and these tests are about the GENERATOR selection.</summary>
    private ProjectFile.Model ModelFor(string assemblyName, string extra = "")
    {
        var project = Write($"{assemblyName}/{assemblyName}.csproj", Library(assemblyName, extra));
        Write($"{assemblyName}/Thing.cs", """
            namespace Probe;
            /// <summary>A type, so the project has something to compile.</summary>
            public sealed class Thing;
            """);
        return ProjectFile.Load(project);
    }

    /// <summary>
    /// The shape of <c>MeshWeaver.Markdown.Collaboration/MarkdownAnnotationParser.cs</c>: a partial
    /// class whose regexes are declared as partial METHODS, which only the generator can implement.
    /// </summary>
    private const string RegexSource = """
        using System.Text.RegularExpressions;
        namespace Probe;
        /// <summary>A parser whose patterns are generated.</summary>
        public static partial class Patterns
        {
            /// <summary>The comment-marker pattern.</summary>
            public static Regex Marker() => MarkerRegex();

            [GeneratedRegex(@"<!--comment:([^:-]+)-->", RegexOptions.Singleline)]
            private static partial Regex MarkerRegex();
        }
        """;

    // ── what the image ships ───────────────────────────────────────────────────────────────────

    [Fact]
    public void TheImageStagesTheSdkGeneratorBesideTheBuilderAndItLOADS()
    {
        var root = StagedGenerators.Locate(null, "/nonexistent-app");
        root.Should().NotBeNull(
            "the image build stages the SDK's implicit analyzers beside the builder; without them "
            + "every [GeneratedRegex] project reports a wall of CS8795 instead");

        // 🚨 The LOAD is the assertion. Both generators are netstandard2.0 assemblies built against a
        // DIFFERENT Roslyn than this process carries (measured: the regex generator asks for
        // Microsoft.CodeAnalysis 4.14.0.0, this repo pins 5.6.0), so "the file is there" proves
        // nothing at all.
        var model = ModelFor("Probe.Locate");
        var set = StagedGenerators.LoadFor(model, null, "/nonexistent-app", [], NullLogger.Instance);

        set.Entries.Should().ContainSingle(e => e.Reason == StagedGenerators.SdkDirectoryName);
        set.Generators.Select(g => g.GetGeneratorType().Name)
            .Should().Contain("RegexGenerator");
        set.Provenance.Should().Contain("targetingPack",
            "the provenance is the staged manifest, not a file listing");
    }

    [Fact]
    public void TheStagedClosureIsOneAssemblyEachAndBothAreARCHITECTURENEUTRAL()
    {
        var root = StagedGenerators.Locate(null, "/nonexistent-app")!;
        var sdk = Path.Combine(root, StagedGenerators.SdkDirectoryName);
        var orleans = Path.Combine(
            root, StagedGenerators.PackagesDirectoryName, "microsoft.orleans.sdk");

        Directory.GetFiles(sdk, "*.dll").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)
            .Should().Equal(["System.Text.RegularExpressions.Generator.dll"],
                "everything that generator references — Roslyn, Immutable, Memory, Buffers, "
                + "Workspaces, Composition.AttributedModel — is in the portal image already, so "
                + "nothing has to travel beside it");
        Directory.GetFiles(orleans, "*.dll").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)
            .Should().Equal(["Orleans.CodeGenerator.dll"],
                "Orleans.Analyzers carries diagnostics and code fixes, not a [Generator], and this "
                + "builder runs no analyzers");

        // 🚨 THE ARCHITECTURE ASSERTION. The SDK's RAZOR compiler is ReadyToRun per RID and had to be
        // staged twice (0xFD1D linux-x64, 0xEC20 osx-arm64); these two are plain MSIL, which is the
        // ONLY reason one staged copy can serve both image architectures. If a future SDK starts
        // crossgenning its analyzers, this goes red on the bump — instead of shipping an arm64 image
        // that silently drops every generated regex.
        foreach (var dll in Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories))
            StagedGenerators.PeMachine(dll).Should().Be(
                StagedGenerators.MsilMachine,
                "a staged generator that is not architecture-neutral cannot be shipped as one copy — "
                + $"{Path.GetFileName(dll)} would have to be staged per RID like the Razor compiler");

        File.Exists(Path.Combine(root, StagedGenerators.ManifestName)).Should().BeTrue(
            "the image must say WHICH generators it carries and from which SDK and Orleans version — "
            + "the image is the pin, and a pin nobody can read is not a pin");
    }

    // ── the positive: the generated half is IN the emitted assembly ────────────────────────────

    [Fact]
    public async Task AGeneratedRegexPartialGETSANIMPLEMENTATION_andTheEmittedRegexWORKS()
    {
        var project = Write("Regexes/Regexes.csproj", Library("Probe.Regexes"));
        Write("Regexes/Patterns.cs", RegexSource);

        var report = await Build(OptionsFor(project));

        report.FatalError.Should().BeNull();
        report.ExitCode.Should().Be(0,
            "a [GeneratedRegex] partial with no generator is CS8795, and that is the whole gap this "
            + "staging closes");
        var result = report.Projects.Single().Result!;
        result.Failure.Should().BeNull();
        result.GeneratedCount.Should().BeGreaterThan(0);

        // 🚨 LOAD IT AND CALL IT. "the compiler returned 0" is not evidence that the partial has an
        // implementation — only the emitted assembly can say, and only calling the method proves the
        // generated body is the regex that was asked for rather than a stub.
        var assembly = Assembly.LoadFrom(result.AssemblyPath!);
        var patterns = assembly.GetType("Probe.Patterns");
        patterns.Should().NotBeNull();

        var generated = patterns!.GetMethod(
            "MarkerRegex", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        generated.Should().NotBeNull(
            "the implementing half of the partial method is what the generator emits; without it the "
            + "type would not have compiled at all");

        var regex = (Regex)patterns.GetMethod("Marker", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, null)!;
        regex.IsMatch("<!--comment:abc-->").Should().BeTrue(
            "the generated implementation must be the pattern the attribute declared");
        regex.Match("<!--comment:abc-->").Groups[1].Value.Should().Be("abc");
    }

    [Fact]
    public async Task AnOrleansProjectGETSITSGENERATEDSERIALIZER_inTheEmittedAssembly()
    {
        var project = Write("Grains/Grains.csproj", Library("Probe.Grains", """
              <ItemGroup>
                <PackageReference Include="Microsoft.Orleans.Sdk" />
              </ItemGroup>
            """));
        Write("Grains/Payload.cs", """
            namespace Probe;
            /// <summary>A serializable payload — exactly what Orleans codegen exists to serve.</summary>
            [Orleans.GenerateSerializer]
            public sealed class Payload
            {
                /// <summary>The value carried across the wire.</summary>
                [Orleans.Id(0)]
                public string Value { get; set; } = "";
            }
            """);

        var report = await Build(OptionsFor(project));

        report.FatalError.Should().BeNull();
        report.ExitCode.Should().Be(0);
        var result = report.Projects.Single().Result!;
        result.Failure.Should().BeNull();
        result.GeneratedCount.Should().BeGreaterThan(0);

        // 🚨 THE ASSERTION THAT MATTERS. Orleans codegen produces NO compile error when it is
        // missing — the project is green either way. The only difference is inside the assembly, so
        // that is where this looks: the generated serializer type, and the assembly-level attribute
        // that registers the whole manifest with the runtime.
        var assembly = Assembly.LoadFrom(result.AssemblyPath!);
        assembly.GetTypes().Select(t => t.FullName)
            .Should().Contain(n => n!.StartsWith("OrleansCodeGen", StringComparison.Ordinal),
                "the serializer, copier and type manifest live in the OrleansCodeGen namespace; an "
                + "assembly without them compiles fine and fails at grain activation");
        assembly.GetCustomAttributesData()
            .Select(a => a.AttributeType.FullName)
            .Should().Contain("Orleans.Serialization.Configuration.TypeManifestProviderAttribute",
                "without the manifest provider attribute the runtime never finds the generated code, "
                + "which is the same outcome as not generating it");
    }

    [Fact]
    public async Task AProjectThatNeedsNoGeneratorIsUNCHANGED()
    {
        // The SDK's implicit analyzers run on every project — including the ones with nothing to
        // generate. That must cost nothing and change nothing, or staging them would be a way to
        // move every assembly in the repo for no reason.
        var project = Write("Plain/Plain.csproj", Library("Probe.Plain"));
        Write("Plain/Thing.cs", """
            namespace Probe;
            /// <summary>An ordinary type.</summary>
            public sealed class Thing
            {
                /// <summary>Its name.</summary>
                public string Name { get; set; } = "";
            }
            """);

        var report = await Build(OptionsFor(project));

        report.ExitCode.Should().Be(0);
        report.Projects.Single().Result!.GeneratedCount.Should().Be(0,
            "a generator with no input must emit nothing at all");
    }

    // ── the negatives: every silent failure gets a name ────────────────────────────────────────

    /// <summary>A staged root carrying only what the caller asks for, so a test can take one half
    /// of the staging away and see what the build says.</summary>
    private string StagedRoot(string name, bool sdk, bool orleans)
    {
        var root = Path.Combine(_root, "_staged", name);
        var shipped = StagedGenerators.Locate(null, "/nonexistent-app")!;
        if (sdk)
            Copy(Path.Combine(shipped, StagedGenerators.SdkDirectoryName),
                Path.Combine(root, StagedGenerators.SdkDirectoryName));
        if (orleans)
            Copy(Path.Combine(shipped, StagedGenerators.PackagesDirectoryName, "microsoft.orleans.sdk"),
                Path.Combine(root, StagedGenerators.PackagesDirectoryName, "microsoft.orleans.sdk"));
        // A root must exist even when both halves are off — that is the mis-staged image this suite
        // is about, not a missing directory.
        Directory.CreateDirectory(Path.Combine(root, StagedGenerators.PackagesDirectoryName));
        return root;
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
    }

    [Fact]
    public async Task WithTheSdkGeneratorUNSTAGED_theBuildNAMESIT_ratherThanEmittingCS8795Noise()
    {
        // 🚨 THE MUTATION PROOF. Take the staging away and the build must not merely go red — it must
        // go red SAYING WHICH GENERATOR DID NOT RUN. A wall of "partial method must have an
        // implementation part" reads like broken source, and that misreading is what cost the sweep
        // its largest remaining block.
        var project = Write("Regexes/Regexes.csproj", Library("Probe.Unstaged"));
        Write("Regexes/Patterns.cs", RegexSource);

        var streamed = new List<LogMessage>();
        var report = await Build(OptionsFor(
            project, staged: StagedRoot("no-sdk", sdk: false, orleans: true), log: streamed));

        report.ExitCode.Should().NotBe(0);

        var named = streamed
            .Select(m => m.Message)
            .Where(l => l.Contains("SOURCE GENERATOR that did not run", StringComparison.Ordinal))
            .ToArray();
        named.Should().NotBeEmpty(
            "the CS8795 wall must be accompanied by the sentence naming its cause");
        named.Should().Contain(l => l.Contains("targeting pack", StringComparison.Ordinal),
            "and it must say WHERE the generator comes from, so the fix is obvious");
        named.Should().Contain(l => l.Contains(StagedGenerators.DirectoryName, StringComparison.Ordinal),
            "and WHERE this image stages it");
    }

    [Fact]
    public void MissingSdkGeneratorMessageNAMESTheGeneratorAndWhereItLooked()
    {
        var message = StagedGenerators.MissingSdkGeneratorMessage(
            "Probe", 7, ["/builder/generators", "/app/generators"]);

        message.Should().Contain("Probe");
        message.Should().Contain("7 of those errors");
        message.Should().Contain("GeneratedRegex");
        message.Should().Contain("/builder/generators");
        message.Should().Contain("/app/generators");
    }

    [Fact]
    public async Task AnOrleansProjectWithNOStagedGeneratorFAILS_ratherThanCompilingAGreenLie()
    {
        // 🚨 The one that would otherwise be invisible: without codegen this project compiles, emits,
        // and reports GREEN. Refusing it is the whole point — the alternative is an assembly that
        // fails at grain activation with nothing pointing back at the build.
        var project = Write("Grains/Grains.csproj", Library("Probe.GrainsUnstaged", """
              <ItemGroup>
                <PackageReference Include="Microsoft.Orleans.Sdk" />
              </ItemGroup>
            """));
        Write("Grains/Payload.cs", """
            namespace Probe;
            /// <summary>A serializable payload.</summary>
            [Orleans.GenerateSerializer]
            public sealed class Payload
            {
                /// <summary>The value.</summary>
                [Orleans.Id(0)]
                public string Value { get; set; } = "";
            }
            """);

        var report = await Build(
            OptionsFor(project, staged: StagedRoot("no-orleans", sdk: true, orleans: false)));

        report.ExitCode.Should().NotBe(0);
        var failure = report.Projects.Single().Result!.Failure;
        failure.Should().NotBeNull();
        failure.Should().Contain("Microsoft.Orleans.Sdk");
        failure.Should().Contain("compile GREEN",
            "the message has to say what the green build would have HIDDEN, not just that a file is "
            + "missing");
        failure.Should().Contain(ProjectFile.Accept.MissingGenerators,
            "and it must name the recorded escape rather than leaving the operator stuck");
    }

    [Fact]
    public async Task TheOrleansRefusalIsAcceptable_deliberatelyAndByName()
    {
        var project = Write("Grains/Grains.csproj", Library("Probe.GrainsAccepted", """
              <ItemGroup>
                <PackageReference Include="Microsoft.Orleans.Sdk" />
              </ItemGroup>
            """));
        Write("Grains/Payload.cs", """
            namespace Probe;
            /// <summary>A serializable payload.</summary>
            [Orleans.GenerateSerializer]
            public sealed class Payload
            {
                /// <summary>The value.</summary>
                [Orleans.Id(0)]
                public string Value { get; set; } = "";
            }
            """);

        var report = await Build(OptionsFor(
            project,
            staged: StagedRoot("accepted", sdk: true, orleans: false),
            log: null,
            accept: ProjectFile.Accept.MissingGenerators));

        report.ExitCode.Should().Be(0, "an acknowledged refusal is a decision, not a wall");
        Assembly.LoadFrom(report.Projects.Single().Result!.AssemblyPath!)
            .GetTypes().Select(t => t.FullName)
            .Should().NotContain(n => n!.StartsWith("OrleansCodeGen", StringComparison.Ordinal),
                "and the accepted build really is the incomplete one it warned about");
    }

    [Fact]
    public void ANamedDirectoryThatIsNotAStagedRootIsAFAILURE_neverASilentFallback()
    {
        var model = ModelFor("Probe.BadRoot");
        var empty = Path.Combine(_root, "_staged", "not-a-root");
        Directory.CreateDirectory(empty);

        Action act = () => StagedGenerators.LoadFor(model, empty, AppDirectory(), [], NullLogger.Instance);

        // 🚨 Falling back to the image's own copy here would report on a generator nobody asked for —
        // the same class of lie as resolving a reference from somewhere the command line does not
        // mention.
        act.Should().Throw<StagedGenerators.MissingGeneratorException>()
            .WithMessage("*is not a staged generator root*");
    }

    [Fact]
    public void ADirectoryOfNONGeneratorAssembliesIsAFAILURE_notAnEmptyRun()
    {
        var model = ModelFor("Probe.NoGenerators");
        var root = Path.Combine(_root, "_staged", "junk");
        var sdk = Path.Combine(root, StagedGenerators.SdkDirectoryName);
        Directory.CreateDirectory(sdk);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "MeshWeaver.ShortGuid.dll"),
            Path.Combine(sdk, "MeshWeaver.ShortGuid.dll"));

        Action act = () => StagedGenerators.LoadFor(model, root, AppDirectory(), [], NullLogger.Instance);

        act.Should().Throw<StagedGenerators.MissingGeneratorException>()
            .WithMessage("*holds no [Generator] type*");
    }

    [Fact]
    public void AGeneratorReachableThroughTWOPackagesRunsONCE()
    {
        // Orleans' codegen is reachable through more than one package id, and a generator that runs
        // twice emits every type twice — CS0101, reported against generated code nobody wrote.
        var root = Path.Combine(_root, "_staged", "twice");
        var shipped = StagedGenerators.Locate(null, "/nonexistent-app")!;
        foreach (var id in new[] { "microsoft.orleans.sdk", "microsoft.orleans.server" })
            Copy(Path.Combine(shipped, StagedGenerators.PackagesDirectoryName, "microsoft.orleans.sdk"),
                Path.Combine(root, StagedGenerators.PackagesDirectoryName, id));

        var model = ModelFor("Probe.Twice", """
              <ItemGroup>
                <PackageReference Include="Microsoft.Orleans.Sdk" />
                <PackageReference Include="Microsoft.Orleans.Server" />
              </ItemGroup>
            """);

        var set = StagedGenerators.LoadFor(model, root, AppDirectory(), [], NullLogger.Instance);

        set.Generators.Select(g => g.GetGeneratorType().FullName)
            .Should().OnlyHaveUniqueItems(
                "the same generator assembly reached through two package ids must be loaded once");
        set.Entries.Should().ContainSingle(
            "the second package id contributes no new assembly, so it contributes no entry");
    }

    // ── --generators, the operator's own, is loud too ──────────────────────────────────────────

    [Fact]
    public async Task GeneratorsSuppliedOnTheCommandLineThatDoNotLOADFailTheBuild()
    {
        // 🚨 The regression this closes. --generators used to route through the node-compile
        // discovery, which reads a failed load as "not a generator" and returns the compilation
        // UNCHANGED — so an operator who supplied a generator built against a different Roslyn got a
        // green build that ran none of it.
        var project = Write("Regexes/Regexes.csproj", Library("Probe.CliGenerators"));
        Write("Regexes/Patterns.cs", RegexSource);
        var junk = Path.Combine(_root, "_cli");
        Directory.CreateDirectory(junk);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "MeshWeaver.ShortGuid.dll"),
            Path.Combine(junk, "MeshWeaver.ShortGuid.dll"));

        var options = OptionsFor(project) with { GeneratorPaths = [junk] };
        var report = await Build(options);

        report.ExitCode.Should().NotBe(0);
        report.Projects.Single().Result!.Failure
            .Should().Contain("no [Generator] type",
                "assemblies that hold no generator compile nothing, and the build must say so rather "
                + "than emitting an assembly that silently lacks what they would have written");
    }
}
