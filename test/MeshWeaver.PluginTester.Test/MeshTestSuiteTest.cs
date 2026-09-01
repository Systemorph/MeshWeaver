using System.Reflection;
using System.Text;
using MeshWeaver.PluginTester;
using MeshWeaver.Security.MeshTest;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The pre-boot service-substitution seam (facility 5): a suite DECLARES the mesh it needs with
/// <c>public static MeshBuilder ConfigureMesh(MeshBuilder)</c>, and the install-and-execute lane
/// boots exactly that mesh for exactly that class.
///
/// <para>The end-to-end case is <c>MeshWeaver.Security.MeshTest</c> — the converted
/// <c>MissingEvaluatorFailsClosedTest</c>, whose premise is a service that must be ABSENT. No
/// additive registration into a shared mesh can express that, which is why the seam has to be a
/// per-suite BOOT rather than a per-run one.</para>
///
/// <para>The other half of this class is the containment: the applicator exists in this binary
/// only, it can only ever CREATE a mesh, and the declaration side needs no reference to it.</para>
/// </summary>
public class MeshTestSuiteTest(ITestOutputHelper output)
{
    private static readonly TimeSpan CaseBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 🚨 THE PROOF. The runner loads a real, foreign assembly — one that carries no xunit, no test
    /// base and no reference to this binary — boots the mesh each of its classes declares, and runs
    /// the cases against it. Every case must PASS; a case classified <c>NeedsMesh</c> here would
    /// mean the lane silently declined to run the thing the facility exists for.
    /// </summary>
    [Fact(Timeout = 300000)]
    public void TheConvertedSuite_BootsItsDeclaredMesh_AndEveryCaseIsGreen()
    {
        var assembly = typeof(MissingEvaluatorFailsClosedTests).Assembly.Location;

        var run = StaticTestRunner.Execute(assembly, [], CaseBudget, new TestWriter(output));

        Assert.Null(run.LoadError);
        foreach (var c in run.Cases)
            output.WriteLine($"{c.Outcome,-10} {c.Name} {c.Error}");
        Assert.Equal(0, run.Failed);
        Assert.Equal(0, run.NeedsMesh);
        // Three cases, from two classes with two DIFFERENT declared meshes — identical evaluator
        // state (none), opposite verdicts, decided only by what each class declared. One shared
        // mesh could not have produced both.
        Assert.Equal(3, run.Passed);
        Assert.True(run.IsGreen);
    }

    /// <summary>
    /// The declaration side carries NO dependency on the applicator: the suite assembly must not
    /// reference <c>mw-plugin-test</c>. That is what makes the seam unusable as a lever — there is
    /// no contract assembly to take, no attribute to apply, and therefore nothing a portal could be
    /// pointed at.
    /// </summary>
    [Fact]
    public void TheSuiteAssembly_ReferencesNothingOfTheApplicator()
    {
        var referenced = typeof(MissingEvaluatorFailsClosedTests).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        Assert.DoesNotContain("mw-plugin-test", referenced);
        Assert.DoesNotContain("MeshWeaver.PluginTester", referenced);
    }

    /// <summary>
    /// 🚨 The containment invariant, asserted rather than asserted-in-prose: nothing on
    /// <see cref="MeshTestSuite"/> ACCEPTS a composed host. Every public member is either a query
    /// over a <see cref="Type"/>/<see cref="MethodInfo"/> or an operation on a mesh this class
    /// itself created. So the worst this code could do if it were ever loaded somewhere it does not
    /// belong is stand up a private throwaway mesh — it cannot reach into a running portal to swap
    /// its <c>IStorageAdapter</c>, because there is no parameter through which a running portal
    /// could be handed to it.
    /// </summary>
    [Fact]
    public void TheFacility_CanNeverTouchAnExistingHost()
    {
        var forbidden = new[]
        {
            typeof(IServiceProvider),
            typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection),
            typeof(MeshWeaver.Messaging.IMessageHub),
        };

        var offenders = typeof(MeshTestSuite)
            .GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance
                        | BindingFlags.DeclaredOnly)
            .OfType<MethodBase>()
            .SelectMany(m => m.GetParameters().Select(p => (Member: m.Name, p.ParameterType)))
            .Where(x => forbidden.Contains(x.ParameterType))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "🚨 A public entry point on MeshTestSuite now ACCEPTS a composed host. The whole "
            + "containment argument for this facility is that it can only CREATE a mesh: "
            + string.Join(", ", offenders.Select(o => $"{o.Member}({o.ParameterType.Name})")));
    }

    /// <summary>
    /// The applicator ships in <c>mw-plugin-test</c> and nowhere else. A shipping project that
    /// referenced the tester would put the discovery code inside a portal image, which is the one
    /// way the "no portal contains it" half of the argument could quietly stop being true.
    /// </summary>
    [Fact]
    public void NoShippingProject_ReferencesTheTester()
    {
        var root = RepoRoot();
        var offenders = new[] { "src", "memex", "clients", "samples" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.csproj", SearchOption.AllDirectories))
            .Where(p => File.ReadAllText(p).Contains("MeshWeaver.PluginTester.csproj", StringComparison.Ordinal))
            .Select(p => Path.GetRelativePath(root, p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "🚨 A shipping project references tools/MeshWeaver.PluginTester, so the pre-boot "
            + "substitution applicator would be present in an image that serves real data. The "
            + "facility's containment rests on the applicator existing only in the tester binary:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// A class with NO declaration is untouched: its parameter-taking cases are still counted and
    /// NAMED as the mesh (area) lane's, never dropped and never silently run against some default
    /// mesh nobody asked for.
    /// </summary>
    [Fact]
    public void AClassWithNoDeclaration_KeepsItsHostedCasesForTheMeshLane()
    {
        var run = RunProbe("NoDeclaration", """
            namespace Probe;
            public static class UndeclaredTests
            {
                public static void APureCase() { }
                public static void AHostedCase(System.IServiceProvider services) { }
            }
            """);

        Assert.Null(run.LoadError);
        Assert.Equal(1, run.Passed);
        Assert.Equal(1, run.NeedsMesh);
        Assert.Contains(run.Cases, c => c.Name == "UndeclaredTests.AHostedCase"
                                        && c.Error!.Contains("the mesh lane runs it", StringComparison.Ordinal));
    }

    /// <summary>
    /// A declared suite whose case takes something this lane cannot supply is NAMED, not run and
    /// not dropped — the report says which half of the estate still owns it.
    /// </summary>
    [Fact]
    public void AnUnbindableParameter_IsNamedRatherThanRun()
    {
        var run = RunProbe("Unbindable", """
            namespace Probe;
            public static class UnbindableTests
            {
                public static MeshWeaver.Mesh.MeshBuilder ConfigureMesh(MeshWeaver.Mesh.MeshBuilder b) => b;
                public static void ACaseTheLaneCannotBind(string whatever) { }
            }
            """);

        Assert.Null(run.LoadError);
        Assert.Equal(1, run.NeedsMesh);
        Assert.Equal(0, run.Failed);
        Assert.Contains(run.Cases, c => c.Error!.Contains(
            "the declared mesh supplies IServiceProvider and IMessageHub only", StringComparison.Ordinal));
        // 🚨 And nothing booted: the class declared a mesh, but no case could use it, so paying for
        // a mesh would be pure cost. Proven by the absence of a boot line in the run's output.
        Assert.DoesNotContain("declared mesh booted", ProbeOutput);
    }

    /// <summary>
    /// 🚨 A declaration that THROWS fails its cases by name. "The suite could not boot" and "the
    /// suite has no cases" must never look alike — a lane that swallowed a boot failure would
    /// report a green run in which nothing ran, which is the exact shape AGENTS.md forbids in a
    /// gate.
    /// </summary>
    [Fact]
    public void ADeclarationThatThrows_FailsTheCase_NeverSkipsIt()
    {
        var run = RunProbe("BootFailure", """
            namespace Probe;
            public static class ExplodingTests
            {
                public static MeshWeaver.Mesh.MeshBuilder ConfigureMesh(MeshWeaver.Mesh.MeshBuilder b)
                    => throw new System.InvalidOperationException("the substitution could not be composed");
                public static void ACase(System.IServiceProvider services) { }
            }
            """);

        Assert.Null(run.LoadError);
        Assert.Equal(1, run.Failed);
        Assert.Equal(0, run.Passed);
        Assert.Equal(0, run.NeedsMesh);
        Assert.False(run.IsGreen);
        var failure = Assert.Single(run.Cases.Where(c => c.Outcome == StaticTestRunner.Outcome.Failed));
        Assert.Contains("the declared mesh did not boot", failure.Error!, StringComparison.Ordinal);
        Assert.Contains("the substitution could not be composed", failure.Error!, StringComparison.Ordinal);
    }

    /// <summary>A declaration is found only in its exact shape; anything else is not one.</summary>
    [Fact]
    public void OnlyTheExactDeclarationShape_Counts()
    {
        Assert.NotNull(MeshTestSuite.FindDeclaration(typeof(MissingEvaluatorFailsClosedTests)));
        Assert.Null(MeshTestSuite.FindDeclaration(typeof(MeshTestSuiteTest)));
        Assert.Null(MeshTestSuite.FindDeclaration(typeof(WrongShape)));
    }

    /// <summary>A near-miss: right name, wrong return type. Not a declaration.</summary>
    private static class WrongShape
    {
        public static string ConfigureMesh(MeshWeaver.Mesh.MeshBuilder builder) => builder.ToString()!;
    }

    private string ProbeOutput = string.Empty;

    /// <summary>
    /// Compiles a probe suite to a real assembly on disk and runs it through the lane — the same
    /// entry point <c>CascadeBuild</c> calls on a package's freshly emitted assembly, so the
    /// classification under test is the shipping one.
    /// </summary>
    private StaticTestRunner.Run RunProbe(string name, string source)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mw-mesh-suite-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, $"Probe.{name}.dll");
            var compilation = CSharpCompilation.Create(
                $"Probe.{name}",
                [CSharpSyntaxTree.ParseText(source)],
                ProbeReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var emit = compilation.Emit(path);
            Assert.True(emit.Success, "the probe suite did not compile: " + string.Join("; ",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

            var writer = new StringWriter();
            var run = StaticTestRunner.Execute(path, [], TimeSpan.FromSeconds(20), writer);
            ProbeOutput = writer.ToString();
            output.WriteLine(ProbeOutput);
            return run;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* a locked temp dir must not fail the test */ }
        }
    }

    private static MetadataReference[] ProbeReferences() =>
    [
        .. ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))),
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>Routes the runner's progress lines into the test's output.</summary>
    private sealed class TestWriter(ITestOutputHelper output) : TextWriter
    {
        private readonly StringBuilder line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                output.WriteLine(line.ToString().TrimEnd('\r'));
                line.Clear();
            }
            else
            {
                line.Append(value);
            }
        }
    }
}
