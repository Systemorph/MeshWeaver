using System.Collections.Immutable;
using System.Threading;
using MeshWeaver.Fixture;
using Microsoft.Extensions.Options;
using MeshWeaver.Hosting.Monolith.TestBase;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Pins the TEST-LOG SINK itself — the thing every CI-only flake investigation depends on and
/// nobody checks, because a dead sink looks exactly like "the code logged nothing".
///
/// <para><b>What broke.</b> <c>XUnitFileLogger.Log</c> drops a record unless
/// <c>XUnitFileOutputHelper.IsInTestMethod()</c> is true, and the only writer of that flag is
/// <c>AutoTestLoggingAttribute.Before</c> → <c>XUnitFileOutputRegistry.GetAnyActiveOutputHelper()</c>.
/// That registry is an <see cref="AsyncLocal{T}"/>, and the registration used to happen inside
/// <c>TestBase.InitializeAsync</c>. <see cref="MonolithMeshTestBase"/> overrides that hook as
/// <c>async</c> — and an AsyncLocal written inside an <c>async</c> method lives in that method's
/// copied <c>ExecutionContext</c> and is discarded when it returns. So the registry was empty by
/// the time xUnit ran <c>Before</c>, <c>SetCurrentTestMethod</c> was never called, and
/// <b>every ILogger record of every Monolith test was silently dropped</b> — at every level, on
/// every platform. The tell is the missing <c>=== TEST START: … ===</c> marker.</para>
///
/// <para><b>What it cost.</b> The Debug channels added to
/// <c>test/MeshWeaver.Query.Test/appsettings.json</c> to diagnose the #993
/// <c>ActivityTrackingHubTest</c> flake produced no output for weeks. #997 repaired one half of
/// that (the appsettings copy race that let MSBuild land the shared Warning-only file instead of
/// the project-local one) and the channels STILL emitted nothing, because the sink downstream of
/// them was closed. Both halves have to hold for a diagnostic to reach CI, so both are pinned:
/// #979/#997 guard the config reaching <c>$(TargetDir)</c> at build time, this guards the sink at
/// run time.</para>
/// </summary>
public class TestOutputLoggingLifecycleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // 🚨 Captured in a FIELD INITIALIZER, which runs before the base constructor and therefore
    // long before AutoTestLoggingAttribute.Before writes this test's TEST_START line.
    //
    // The first version of the window pin below read the file WHOLE, and it passed with the
    // mechanism deleted — because a previous green run of this very test had left a matching line
    // in a file that is append-only and shared by every test host on the machine. That is a
    // verification step that cannot fail, in a change whose entire subject is verification steps
    // that cannot fail. Anchoring at construction is what makes the assertion about THIS run.
    private readonly long traceLengthAtConstruction = CurrentTraceLength();

    /// <summary>
    /// The lifecycle pin: a test class whose <c>InitializeAsync</c> is <c>async</c> (which
    /// <see cref="MonolithMeshTestBase"/>'s is) must still have a LIVE log sink by the time its
    /// body runs. Both assertions were false before the registration moved to the constructor.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public void AsyncInitializeAsync_StillLeavesTheTestLogSinkOpen()
    {
        XUnitFileOutputRegistry.GetAnyActiveOutputHelper().Should().NotBeNull(
            "the output-helper registry is an AsyncLocal — registering it from an `async` "
            + "InitializeAsync discards the write, which leaves AutoTestLoggingAttribute.Before "
            + "with nothing to mark and silently kills logging for the whole test");

        FileOutput.IsInTestMethod().Should().BeTrue(
            "XUnitFileLogger drops every record unless the helper is inside a test method; this "
            + "flag being false is what made the #993 Debug channels emit nothing on CI");
    }

    /// <summary>
    /// The WINDOW pin (#2495). Until this landed, the per-test phase trace was written only by
    /// <see cref="MonolithMeshTestBase"/>, so an assembly built on a different base — Orleans's,
    /// say — contributed almost nothing to the one log CI keeps. Measured on CI run 33062668925,
    /// shard 3: <c>MeshWeaver.Hosting.Orleans.Test</c> ran 208 tests over 272 seconds and wrote
    /// FOUR lines, while its three Monolith-derived shard-mates wrote 819, 606 and 1789. A fault
    /// recorded in that file could therefore not be placed in time at all, and a host killed at
    /// the wall-clock cap (which writes no trx whatsoever) left nothing that named the test it
    /// was stuck in.
    ///
    /// <para>The markers now come from <c>AutoTestLoggingAttribute</c>, which every
    /// <see cref="TestBase"/> subclass in every project already carries — so this assertion holds
    /// for the whole suite, not for one base class.</para>
    /// </summary>
    [Fact(Timeout = 20_000)]
    public void TheTestWindow_IsWrittenToTheOneLogCiKeeps()
    {
        // pid too: every test host in a CI shard appends to this one file, so without it a
        // concurrent run of the same test elsewhere would satisfy the assertion.
        ReadTraceTailFrom(traceLengthAtConstruction).Should().Contain(
            $"pid={Environment.ProcessId} [{nameof(TestOutputLoggingLifecycleTest)}] "
            + $"TEST_START {nameof(TheTestWindow_IsWrittenToTheOneLogCiKeeps)}",
            "without a TEST_START line the fault records in this file cannot be attributed to a "
            + "test, and a killed host leaves nothing that names where it wedged");
    }

    /// <summary>
    /// The FAULT-SINK pin (#2495), and the asymmetry it closes.
    ///
    /// <para>There are two xUnit loggers and they split the process by SERVICE PROVIDER, not by
    /// importance: <c>XUnitFileLogger</c> serves the <see cref="TestBase"/> container,
    /// <see cref="XUnitLogger"/> serves everything built by a HOST. In an Orleans test that second
    /// set is the silo, the client, and every mesh hub and grain inside them — essentially the
    /// whole system under test. Only the first wrote fault records, so an exception logged
    /// anywhere in a silo reached NO artifact CI keeps.</para>
    ///
    /// <para>🚨 The probe runs with <b>no active test output helper</b>, on a thread whose
    /// ExecutionContext flow is suppressed, because that is the case that matters: fixture init,
    /// teardown and reactive continuations on background schedulers are precisely when a wedge
    /// faults, and precisely when every other sink is closed.</para>
    /// </summary>
    [Fact(Timeout = 20_000)]
    public void AFaultLoggedWithNoActiveTest_StillReachesTheOneLogCiKeeps()
    {
        // A standalone logging container, so the pin exercises the LOGGER rather than whatever
        // the mesh happens to register: the default filter (MinLevel = Information) lets an Error
        // through, which is all this assertion needs.
        using var logging = new ServiceCollection().AddLogging().BuildServiceProvider();

        var logger = new XUnitLogger(
            "MeshWeaver.Test.FaultSinkPin",
            new TestOutputHelperAccessor(),           // no helper — the wedge case
            new LoggerExternalScopeProvider(),
            logging.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>());

        var marker = "fault-sink-pin-" + Guid.NewGuid().ToString("N");
        var before = CurrentTraceLength();

        // SuppressFlow so the new thread does NOT inherit this test's AsyncLocal output-helper
        // registration — otherwise the logger would find a helper and the probe would be testing
        // the wrong path.
        using (ExecutionContext.SuppressFlow())
        {
            var thread = new Thread(() => logger.Log(
                LogLevel.Error, default, marker, new InvalidOperationException(marker),
                (state, _) => state)) { IsBackground = true };
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("the probe thread must finish");
        }

        ReadTraceTailFrom(before).Should().Contain(marker,
            "a Warning-or-worse record carrying an exception must reach the fault log even when "
            + "no test output helper is active — that is the only sink a wedge can still write to");
    }

    private static long CurrentTraceLength()
        => new FileInfo(TestTraceLog.Path) is { Exists: true } file ? file.Length : 0L;

    /// <summary>
    /// Reads only what this test appended. The file is shared by every test host in a CI shard
    /// (and by every concurrent local run), so reading it whole would be both slow and ambiguous.
    /// </summary>
    private static string ReadTraceTailFrom(long offset)
    {
        using var stream = new FileStream(
            TestTraceLog.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The channel pin: this project raises specific channels to Debug in its own
    /// <c>appsettings.json</c> precisely to diagnose #993. Assert the level filter actually
    /// resolves to Debug for the tracking channel — if the shared Warning-only appsettings ever
    /// wins the copy again, or the project file loses its overrides, this fails loudly instead of
    /// producing another silent, evidence-free CI run.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public void ActivityTrackingDebugChannel_IsActuallyEnabled()
    {
        var factory = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>();
        foreach (var channel in ImmutableList.Create(
                     "MeshWeaver.Graph.ActivityTracking",
                     "MeshWeaver.Hosting.MeshNodeStreamCache"))
        {
            factory.CreateLogger(channel).IsEnabled(LogLevel.Debug).Should().BeTrue(
                $"'{channel}' is raised to Debug in test/MeshWeaver.Query.Test/appsettings.json "
                + "for the #993 investigation; a Warning-only config here means the diagnostic is "
                + "dead again");
        }
    }
}
