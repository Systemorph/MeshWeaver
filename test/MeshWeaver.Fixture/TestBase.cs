using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Fixture;

/// <summary>
/// Base class for tests that uses xUnit output capturing with file logging.
/// All test output is captured to both xUnit test output and log files.
/// </summary>
[AutoTestLogging]
public class TestBase : ServiceSetup, IAsyncLifetime
{
    /// <summary>The raw xUnit output helper for the running test.</summary>
    protected readonly ITestOutputHelper Output;
    /// <summary>The file-and-xUnit output helper that captures test output to both sinks.</summary>
    protected readonly XUnitFileOutputHelper FileOutput;
    private ILogger? _logger;

    /// <summary>
    /// Initializes a new test instance, creating the file output helper and wiring file logging.
    /// </summary>
    /// <param name="output">The xUnit output helper for the running test.</param>
    protected TestBase(ITestOutputHelper output)
    {
        Output = output;
        FileOutput = new XUnitFileOutputHelper(output, GetType().Name);

        // 🚨 Register HERE, in the constructor — not in InitializeAsync.
        //
        // XUnitFileOutputRegistry is backed by an AsyncLocal, and an AsyncLocal written
        // inside an `async` method is confined to that method's copied ExecutionContext:
        // it is DISCARDED when the method returns. MonolithMeshTestBase overrides
        // InitializeAsync as `async` (it awaits hosted-service starts and access setup),
        // so registering from base.InitializeAsync() left the registry EMPTY by the time
        // xUnit invoked AutoTestLoggingAttribute.Before. Before therefore never called
        // SetCurrentTestMethod, XUnitFileOutputHelper.IsInTestMethod() stayed false for
        // the whole test, and XUnitFileLogger.Log dropped EVERY record — so no ILogger
        // line from any MonolithMeshTestBase test ever reached xUnit's captured output,
        // at any level, on any platform. That is why the Debug channels added to
        // test/MeshWeaver.Query.Test/appsettings.json to diagnose #993 emitted nothing
        // even after #997 fixed the appsettings copy race: the file was landing, the
        // level filter said enabled, and the sink was closed.
        //
        // The constructor runs synchronously on the runner's own flow, so the write
        // survives to Before — which is what the registry's own doc comment always
        // claimed ("the value set in a test's ctor flows to its method"). Pinned by
        // TestOutputLoggingLifecycleTest.
        XUnitFileOutputRegistry.Register(this, FileOutput);

        // Configure file logging integration. Log-level filters are bound
        // from appsettings.json in ServiceSetup (see ServiceSetup.CreateServiceCollection
        // → logging.AddConfiguration("Logging")). Flip the shared
        // test/appsettings.json's "Logging:LogLevel:MeshWeaver" to "Debug"
        // for hang-hunt runs.
        //
        // 🚨 The provider closes over a LOCAL, never over `this`. Every logger the mesh hands out
        // holds this closure, and a logger is held by whatever outlives the mesh for a while: a
        // pending timer's captured ExecutionContext, IHttpClientFactory's handler-expiry timer.
        // A closure over `this` made each of those reach the test instance, and through its
        // Services and MeshBuilder the whole disposed mesh, until the timer fired. Measured in a
        // Memex.Portal.Shared.Test heap dump: 84 meshes' type registries live with about 20
        // running (TestLoggerDoesNotRootTheTestTest).
        var fileOutput = FileOutput;
        Services.AddSingleton(fileOutput);

        // 🚨 Timeout == error (TimeoutTerminalGuard.cs): every product-side timeout terminal that
        // fires in this test's container is recorded here, and AutoTestLoggingAttribute.After
        // turns an undeclared one into the test's failure when the guard is armed. One instance
        // per test container — never static.
        var timeoutTerminals = new TimeoutTerminalRecorder();
        Services.AddSingleton(timeoutTerminals);
        Services.AddSingleton<MeshWeaver.Messaging.ITimeoutTerminalObserver>(timeoutTerminals);
        Services.AddLogging(logging =>
        {
            logging.ClearProviders(); // Remove default providers
            logging.Services.AddSingleton<ILoggerProvider>(serviceProvider =>
                new XUnitFileLoggerProvider(() => fileOutput, serviceProvider));
            TestLogLevel.ApplyOverride(logging); // MW_TEST_LOG_LEVEL past the factory's rules
        });
        
    }

    /// <summary>
    /// Builds the service provider, registers this test for cross-class output access, and logs
    /// the test-class start marker.
    /// </summary>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
    public virtual ValueTask InitializeAsync()
    {
        Initialize();
        SetOutputHelper(FileOutput);

        // (Registration happens in the constructor — see the note there. Doing it from
        // here is what broke test logging for every async-InitializeAsync test base.)

        // Get logger after service provider is built
        _logger = ServiceProvider.GetService(typeof(ILogger<TestBase>)) as ILogger<TestBase>;
        
        
        // Log test start with class info
        var className = GetType().Name;
        FileOutput.WriteLine($"=== TEST CLASS INIT: {className} ===");
        _logger?.LogInformation("=== TEST CLASS INIT: {TestClass} ===", className);
        
        return ValueTask.CompletedTask;
    }




    private System.Collections.Immutable.ImmutableList<(TimeoutTerminalRecorder Recorder, int Watermark)> timeoutTerminalWindow =
        System.Collections.Immutable.ImmutableList<(TimeoutTerminalRecorder, int)>.Empty;
    private bool timeoutTerminalWindowOpen;

    /// <summary>
    /// Opens this test's timeout-terminal window. Read off <see cref="ServiceSetup.ServiceProvider"/>
    /// rather than the instance registered in the constructor: a shared-mesh test class serves every
    /// test from the FIRST instance's container, so that container's recorder is the one the mesh
    /// reports to, and the watermark keeps an earlier test's terminals out of this one's verdict.
    /// Every <see cref="ITimeoutTerminalSource"/> the test holds in an instance field (an Orleans
    /// cluster fixture: its silos and client host each have a container of their own) adds its
    /// recorders, each with its own watermark.
    /// </summary>
    internal void BeginTimeoutTerminalWindow()
    {
        var recorders = new List<TimeoutTerminalRecorder>();
        if (ServiceProvider?.GetService<TimeoutTerminalRecorder>() is { } own)
            recorders.Add(own);
        recorders.AddRange(HeldTimeoutTerminalSources().SelectMany(source => source.TimeoutTerminalRecorders));
        timeoutTerminalWindow = recorders
            .Distinct()
            .Select(recorder => (recorder, recorder.Fired.Count))
            .ToImmutableList();
        timeoutTerminalWindowOpen = true;
    }

    /// <summary>The <see cref="ITimeoutTerminalSource"/>s held in this test's instance fields, base classes included.</summary>
    private IEnumerable<ITimeoutTerminalSource> HeldTimeoutTerminalSources()
    {
        for (var type = GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance
                                                 | System.Reflection.BindingFlags.Public
                                                 | System.Reflection.BindingFlags.NonPublic
                                                 | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (!typeof(ITimeoutTerminalSource).IsAssignableFrom(field.FieldType)
                    && !field.FieldType.IsAssignableFrom(typeof(ITimeoutTerminalSource)))
                    continue;
                if (field.GetValue(this) is ITimeoutTerminalSource source)
                    yield return source;
            }
        }
    }

    /// <summary>
    /// Closes this test's timeout-terminal window: names every terminal that fired during the test
    /// in its output and the shard trace, and — when the guard is armed — returns the failure the
    /// caller throws. <c>null</c> when nothing is wrong or nothing was recorded.
    /// </summary>
    /// <param name="method">The test method.</param>
    internal string? EndTimeoutTerminalWindow(System.Reflection.MethodInfo method)
    {
        var window = timeoutTerminalWindow;
        var open = timeoutTerminalWindowOpen;
        timeoutTerminalWindow = System.Collections.Immutable.ImmutableList<(TimeoutTerminalRecorder, int)>.Empty;
        timeoutTerminalWindowOpen = false;
        var declared = TimeoutTerminalVerdict.DeclaredKinds(method);
        var fired = window
            .SelectMany(entry => entry.Recorder.Fired.Skip(entry.Watermark))
            .ToList();
        var testName = $"{method.DeclaringType?.Name}.{method.Name}";
        foreach (var terminal in fired)
        {
            var declaredMark = declared.Contains(terminal.Kind) ? "declared" : "UNDECLARED";
            FileOutput.WriteLine($"=== TIMEOUT TERMINAL ({declaredMark}): {terminal.Kind} at '{terminal.Origin}' ===");
            TestTraceLog.AppendPhase(method.DeclaringType?.Name ?? "UnknownTest", "TIMEOUT_TERMINAL",
                extra: $"{method.Name} kind={terminal.Kind} {declaredMark} origin={terminal.Origin}");
        }
        if (!open || window.IsEmpty || !TimeoutTerminalVerdict.IsArmed(method))
            return null;
        return TimeoutTerminalVerdict.Evaluate(testName, fired, declared);
    }

    /// <summary>
    /// Logs the test-class dispose marker, unregisters this test, and disposes the file output helper.
    /// </summary>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
    public virtual ValueTask DisposeAsync()
    {
        // Log test end with class info
        var className = GetType().Name;
        FileOutput.WriteLine($"=== TEST CLASS DISPOSE: {className} ===");
        _logger?.LogInformation("=== TEST CLASS DISPOSE: {TestClass} ===", className);


        // Unregister this test instance
        XUnitFileOutputRegistry.Unregister(this);

        SetOutputHelper(null);
        FileOutput.Dispose();

        // NOTE: Earlier version called Dispose() here to release the DI container
        // SP at end of every [Fact]. That broke 40+ tests across PathResolution,
        // Hosting.Monolith, FutuRe, Graph etc. — too many test classes hold
        // singleton handles that are read AFTER DisposeAsync (e.g. via test
        // fixture DI patterns or hub-disposal callbacks). Reverting until a
        // safer mechanism (per-class collectible AssemblyLoadContext, or
        // explicit opt-in flag) is in place.

        return ValueTask.CompletedTask;
    }

    

}

/// <summary>
/// Base class for tests that share a <typeparamref name="TFixture"/> across the test class,
/// routing each test's output through the shared fixture.
/// </summary>
/// <typeparam name="TFixture">The shared fixture type.</typeparam>
public class TestBase<TFixture> : IDisposable
    where TFixture : BaseFixture, new()
{
    /// <summary>The shared fixture for the test class.</summary>
    public TFixture Fixture { get; }

    /// <summary>
    /// Initializes a new test instance bound to the shared fixture and wires the test's output helper.
    /// </summary>
    /// <param name="fixture">The shared fixture instance.</param>
    /// <param name="output">The xUnit output helper for the running test.</param>
    protected TestBase(TFixture fixture, ITestOutputHelper output)
    {
        fixture.SetOutputHelper(output);
        Fixture = fixture;
    }

    /// <summary>
    /// Detaches this test's output helper from the shared fixture and disposes the fixture.
    /// </summary>
    public virtual void Dispose()
    {
        Fixture.SetOutputHelper(null);
        Fixture.Dispose();
    }
}


