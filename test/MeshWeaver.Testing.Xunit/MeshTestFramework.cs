using System.Reflection;
using Xunit.Sdk;
using Xunit.v3;

namespace MeshWeaver.Testing.Xunit;

/// <summary>
/// The execution host: xunit v3's own test framework, with ONE thing changed — a
/// <see cref="TestCollectionScope"/> is opened around each test collection so an expensive fixture
/// (a booted mesh) can be stood up once for the collection instead of once per case.
///
/// <para>Opt in per test assembly:</para>
/// <code>[assembly: Xunit.TestFramework(typeof(MeshWeaver.Testing.Xunit.MeshTestFramework))]</code>
///
/// <para><b>This is a host substitution, not a test vocabulary.</b> Discovery, <c>[Theory]</c> data
/// enumeration (<c>[InlineData]</c>, <c>[MemberData]</c>, <c>TheoryData&lt;&gt;</c>), <c>Skip=</c>,
/// traits, <c>[Collection]</c> grouping, <c>ITestOutputHelper</c>, timeouts, parallelism and
/// per-row pass/fail reporting all remain xunit's, untouched. Every one of the estate's 1,778
/// <c>[InlineData]</c> and 404 <c>[Theory]</c> declarations keeps working with no edit, and each
/// row keeps its own name and its own verdict — because xunit, not this code, is still the thing
/// producing them.</para>
///
/// <para><b>Blast radius.</b> The attribute is assembly-wide, so an assembly that declares it sends
/// every one of its cases through <see cref="MeshTestAssemblyRunner"/>. It is therefore adopted one
/// assembly at a time, and an assembly that does not declare it is bit-for-bit unaffected.</para>
/// </summary>
public class MeshTestFramework : XunitTestFramework
{
    private readonly string? configFileName;

    /// <summary>Initializes the framework with xunit's default configuration discovery.</summary>
    public MeshTestFramework() => configFileName = null;

    /// <summary>Initializes the framework against a specific <c>xunit.runner.json</c>.</summary>
    /// <param name="configFileName">The configuration file xunit resolved for the assembly.</param>
    public MeshTestFramework(string? configFileName)
        : base(configFileName!) => this.configFileName = configFileName;

    /// <inheritdoc/>
    public override string TestFrameworkDisplayName =>
        base.TestFrameworkDisplayName + " [MeshWeaver collection-scoped fixtures]";

    /// <inheritdoc/>
    protected override ITestFrameworkExecutor CreateExecutor(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return new MeshTestFrameworkExecutor(
            new XunitTestAssembly(assembly, configFileName, assembly.GetName().Version));
    }
}

/// <summary>
/// Hands execution to <see cref="MeshTestAssemblyRunner"/> instead of xunit's stock assembly
/// runner. Nothing else differs from <see cref="XunitTestFrameworkExecutor"/>.
/// </summary>
/// <param name="testAssembly">The assembly under test.</param>
public class MeshTestFrameworkExecutor(IXunitTestAssembly testAssembly)
    : XunitTestFrameworkExecutor(testAssembly)
{
    /// <inheritdoc/>
    public override async ValueTask RunTestCases(
        IReadOnlyCollection<IXunitTestCase> testCases,
        IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions,
        CancellationToken cancellationToken) =>
        await MeshTestAssemblyRunner.MeshInstance
            .Run(TestAssembly, testCases, executionMessageSink, executionOptions, cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>
/// xunit's assembly runner with one override: <see cref="RunTestCollection"/> brackets the
/// collection in a <see cref="TestCollectionScope"/>.
///
/// <para>The scope is opened BEFORE the collection's cases run and disposed AFTER the last one
/// finishes — including when a case throws, is skipped, or the run is cancelled. That is the
/// lifetime the estate's <c>ShareMeshAcrossTests</c> flag never had: its static per-class cache
/// had no end, so a shared mesh outlived its tests and interfered with the next class's.</para>
/// </summary>
public class MeshTestAssemblyRunner : XunitTestAssemblyRunner
{
    /// <summary>The singleton this framework runs assemblies with.</summary>
    public static MeshTestAssemblyRunner MeshInstance { get; } = new();

    /// <inheritdoc/>
    protected override async ValueTask<RunSummary> RunTestCollection(
        XunitTestAssemblyRunnerContext ctxt,
        IXunitTestCollection testCollection,
        IReadOnlyCollection<IXunitTestCase> testCases)
    {
        ArgumentNullException.ThrowIfNull(testCollection);

        // 🚨 Opened here and awaited-through, never captured into a field. The scope's AsyncLocal
        // is written on THIS method's execution context, so it is visible to every case of this
        // collection and to nothing else — which is what makes two collections running in parallel
        // (xunit's default) see their own fixture rather than each other's.
        var scope = TestCollectionScope.Begin(
            testCollection.TestCollectionDisplayName ?? testCollection.UniqueID);
        try
        {
            return await base.RunTestCollection(ctxt, testCollection, testCases).ConfigureAwait(false);
        }
        finally
        {
            // A disposal failure must not be swallowed, but it also must not erase the collection's
            // verdicts: the summary is already reported by the time this runs, and xunit surfaces a
            // throw from here as a collection cleanup failure — which is exactly what a leaked mesh
            // is. Nothing is caught.
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }
}
