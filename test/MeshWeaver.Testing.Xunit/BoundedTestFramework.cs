using System.Reflection;
using Xunit.Sdk;
using Xunit.v3;

namespace MeshWeaver.Testing.Xunit;

/// <summary>
/// The default execution host of every test assembly in the estate: xunit v3's own test framework
/// with the per-test bound (<see cref="MethodBound"/>) and the timeout witness
/// (<see cref="TimeoutWitness"/>) added. Nothing else differs.
///
/// <para><b>How an assembly gets it.</b> <c>test/Directory.Build.props</c> declares
/// <c>[assembly: Xunit.TestFramework(typeof($(MeshWeaverTestFramework)))]</c> for every test project,
/// this type by default. Satellites inherit it through the same props, which they import for their
/// <c>*.Test</c> projects. A project that names its own framework in source keeps it, and the props
/// then emit nothing. <see cref="MeshTestFramework"/> carries the same bound.</para>
/// </summary>
public class BoundedTestFramework : XunitTestFramework
{
    private readonly string? configFileName;

    /// <summary>Initializes the framework with xunit's default configuration discovery.</summary>
    public BoundedTestFramework() => configFileName = null;

    /// <summary>Initializes the framework against a specific <c>xunit.runner.json</c>.</summary>
    /// <param name="configFileName">The configuration file xunit resolved for the assembly.</param>
    public BoundedTestFramework(string? configFileName)
        : base(configFileName!) => this.configFileName = configFileName;

    /// <inheritdoc/>
    public override string TestFrameworkDisplayName =>
        base.TestFrameworkDisplayName + " [MeshWeaver per-test bound]";

    /// <summary>The assembly model handed to the executor; shared with <see cref="MeshTestFramework"/>.</summary>
    /// <param name="assembly">The assembly under test.</param>
    protected IXunitTestAssembly CreateTestAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return new XunitTestAssembly(assembly, configFileName, version: assembly.GetName().Version);
    }

    /// <inheritdoc/>
    protected override ITestFrameworkExecutor CreateExecutor(Assembly assembly) =>
        new BoundedTestFrameworkExecutor(CreateTestAssembly(assembly));
}

/// <summary>
/// xunit's executor with the per-test bound applied to the cases before they run, and every message
/// passed through a <see cref="TimeoutWitness"/>.
/// </summary>
/// <param name="testAssembly">The assembly under test.</param>
public class BoundedTestFrameworkExecutor(IXunitTestAssembly testAssembly)
    : XunitTestFrameworkExecutor(testAssembly)
{
    /// <inheritdoc/>
    public override ValueTask RunTestCases(
        IReadOnlyCollection<IXunitTestCase> testCases,
        IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions,
        CancellationToken cancellationToken) =>
        base.RunTestCases(testCases, Bound(TestAssembly, testCases, executionMessageSink), executionOptions, cancellationToken);

    /// <summary>
    /// Applies the assembly's bound to <paramref name="testCases"/> and returns the sink to run them
    /// with. One place, so <see cref="MeshTestFrameworkExecutor"/> cannot drift from it.
    /// </summary>
    /// <param name="assembly">The assembly under test.</param>
    /// <param name="testCases">The cases about to run.</param>
    /// <param name="sink">The run's message sink.</param>
    public static IMessageSink Bound(IXunitTestAssembly assembly, IReadOnlyCollection<IXunitTestCase> testCases, IMessageSink sink)
    {
        var bound = MethodBound.Resolve(assembly);
        MethodBound.Apply(testCases, bound);
        return bound > 0 ? new TimeoutWitness(sink, bound) : sink;
    }
}
