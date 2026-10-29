using System;
using System.IO;
using System.Linq;
using MeshWeaver.Graph.Test;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// #7444: two NodeTypes ship different builds of the same source generator. Generators load into
/// the process-wide default load context, which holds one build per name, so the second build is
/// REFUSED, naming both NodeTypes and both builds, and the compile fails. The same build asked for
/// twice (from another path) is admitted: that is the negative control. Offline: the generator
/// builds are emitted here, each under a name unique to the test so no other test shares it.
/// </summary>
public sealed class SourceGeneratorBuildConflictTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-genconflict-" + Guid.NewGuid().ToString("N"));

    public SourceGeneratorBuildConflictTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Emits a generator assembly named <paramref name="name"/> whose output says <paramref name="marker"/>.</summary>
    private string EmitGenerator(string folder, string name, string marker)
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".dll");
        var emitted = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText(
                "using Microsoft.CodeAnalysis;\n"
                + "[Generator] public sealed class Gen : ISourceGenerator {\n"
                + "  public void Initialize(GeneratorInitializationContext context) { }\n"
                + $"  public void Execute(GeneratorExecutionContext context) => context.AddSource(\"g.cs\", \"public static class FromGen {{ public const string Build = \\\"{marker}\\\"; }}\");\n"
                + "}")],
            CompileReferences.Default,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true)).Emit(path);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return path;
    }

    private static string UniqueName() => "MwConflictGen" + Guid.NewGuid().ToString("N");

    [Fact]
    public void ASecondDifferentBuild_IsRefused_NamingBothNodeTypesAndBothBuilds()
    {
        var name = UniqueName();
        var first = EmitGenerator("v1", name, "one");
        var second = EmitGenerator("v2", name, "two");
        var ledger = new SourceGeneratorLedger();

        SourceGeneratorLoader.Discover([first], ledger, "App/TypeA", NullLogger.Instance).Should().ContainSingle();

        var refusal = Assert.Throws<SourceGeneratorBuildConflictException>(
            () => SourceGeneratorLoader.Discover([second], ledger, "App/TypeB", NullLogger.Instance));

        refusal.NodePath.Should().Be("App/TypeB", "the refused compile is the one asking for the second build");
        refusal.Loaded.LoadedFor.Should().Be("App/TypeA");
        refusal.Requested.LoadedFor.Should().Be("App/TypeB");
        refusal.Loaded.Mvid.Should().NotBe(refusal.Requested.Mvid);
        refusal.Message.Should().Contain("App/TypeA").And.Contain("App/TypeB")
            .And.Contain(refusal.Loaded.Mvid.ToString("D")).And.Contain(refusal.Requested.Mvid.ToString("D"))
            .And.Contain(first).And.Contain(second);
    }

    [Fact]
    public void ASecondDifferentBuild_FailsTheCompile_ThroughTheGeneratorPipeline()
    {
        var name = UniqueName();
        var first = EmitGenerator("p1", name, "one");
        var second = EmitGenerator("p2", name, "two");
        var ledger = new SourceGeneratorLedger();
        var ct = TestContext.Current.CancellationToken;

        var compiled = GeneratorPipeline.RunSourceGenerators(Authored(), [first], ledger, "App/TypeA", NullLogger.Instance, ct);
        compiled.SyntaxTrees.Should().Contain(t => t.ToString().Contains("\"one\""), "the first build ran");

        var refusal = Assert.Throws<SourceGeneratorBuildConflictException>(
            () => GeneratorPipeline.RunSourceGenerators(Authored(), [second], ledger, "App/TypeB", NullLogger.Instance, ct));
        refusal.Should().BeAssignableTo<CompilationException>(
            "a CompilationException is what the runtime compile parks at CompilationStatus=Error with its message");
    }

    [Fact]
    public void ADifferentBuildLoadedOutsideThisLedger_IsStillRefused_NamingWhereItLoadedFrom()
    {
        var name = UniqueName();
        var first = EmitGenerator("o1", name, "one");
        var second = EmitGenerator("o2", name, "two");

        SourceGeneratorLoader.Discover([first], new SourceGeneratorLedger(), "App/TypeA", NullLogger.Instance)
            .Should().ContainSingle();

        // A fresh ledger knows nothing of the first load; the default load context does.
        var refusal = Assert.Throws<SourceGeneratorBuildConflictException>(
            () => SourceGeneratorLoader.Discover([second], new SourceGeneratorLedger(), "App/TypeB", NullLogger.Instance));
        refusal.Loaded.Path.Should().Be(first);
        refusal.Message.Should().Contain("App/TypeB").And.Contain(first);
    }

    [Fact]
    public void NegativeControl_TheSameBuildTwice_FromAnotherPath_IsAdmitted()
    {
        var name = UniqueName();
        var first = EmitGenerator("s1", name, "one");
        var copyDir = Path.Combine(root, "s2");
        Directory.CreateDirectory(copyDir);
        var copy = Path.Combine(copyDir, Path.GetFileName(first));
        File.Copy(first, copy);
        var ledger = new SourceGeneratorLedger();

        var a = SourceGeneratorLoader.Discover([first], ledger, "App/TypeA", NullLogger.Instance);
        var b = SourceGeneratorLoader.Discover([copy], ledger, "App/TypeB", NullLogger.Instance);

        a.Should().ContainSingle();
        b.Should().ContainSingle("the same bytes are the same build, whichever path they come from");
        b.Single().GetGeneratorType().Assembly.Should().BeSameAs(a.Single().GetGeneratorType().Assembly,
            "the already-loaded build is reused, never loaded a second time");
        System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies
            .Count(x => x.GetName().Name == name).Should().Be(1);
    }

    private static CSharpCompilation Authored() =>
        CSharpCompilation.Create("Authored" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText("public static class Probe { }")],
            CompileReferences.Default,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
}
