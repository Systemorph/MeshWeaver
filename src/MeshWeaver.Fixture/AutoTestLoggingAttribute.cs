using System.Diagnostics;
using System.Reflection;
using Xunit;
using Xunit.v3;

namespace MeshWeaver.Fixture;

/// <summary>
/// Automatically logs test method start and end markers to help correlate test execution with debug logs.
/// This attribute is automatically applied to all test methods through the TestBase class.
///
/// <para>🚨 The markers go to TWO sinks, and the second one is the point. The xUnit output helper
/// is per-test and reaches the trx — useful, but only for a host that lives long enough to write
/// one. <see cref="TestTraceLog"/> is the single file CI keeps per shard, and it is the only
/// evidence that survives a host killed at the wall-clock cap (<c>exit=124</c>, no trx at all) or
/// dying on a signal.</para>
///
/// <para><b>Why this lives here rather than on a mesh test base.</b> Until #2495 the per-test
/// phase trace was written ONLY by <c>MonolithMeshTestBase</c>. Measured on CI run 33062668925,
/// shard 3: the <c>MeshWeaver.Hosting.Orleans.Test</c> host ran <b>208 tests over 272 seconds and
/// contributed FOUR lines</b> to that file, while its three shard-mates — all Monolith-derived —
/// contributed 819, 606 and 1789. So an Orleans failure could not be placed in time at all: the
/// trace covered the run but not the window, which is exactly what issue #2495 records. This
/// attribute is already applied to every <see cref="TestBase"/> subclass in every project, so
/// writing the window markers HERE is what makes the coverage universal instead of one base
/// class's private habit.</para>
///
/// <para>What the two lines buy, concretely: a <c>[FAULT]</c> record (the fault sink writes the
/// exception + stack to the same file) can be bracketed by the test that was running when it
/// landed; a hung host's last <c>TEST_START</c> with no matching <c>TEST_END</c> NAMES the stuck
/// test, which no trx can do; and under intra-assembly parallelism (Orleans.Test runs
/// <c>maxParallelThreads: 4</c>) the overlapping <c>TEST_START</c>/<c>TEST_END</c> brackets show
/// which OTHER classes were live during a failure.</para>
/// </summary>
public class AutoTestLoggingAttribute : BeforeAfterTestAttribute
{
    // One failure message can be a multi-KB assertion dump. The trace is a per-shard artifact
    // shared by every test host, so the message is clamped and flattened to ONE line — the file's
    // value is that `grep TEST_END` yields one row per test.
    private const int MaxFailureChars = 400;

    // 🚨 The elapsed time is MEASURED here, not read off TestResultState.ExecutionTime — and that
    // is not defensive taste, it is a wrong number avoided. xUnit v3.2.2 documents ExecutionTime
    // as "the time spent executing the test, in seconds", and it is not: for a test the trx
    // records at duration="00:00:01.5306690" it reports 1523.705, i.e. MILLISECONDS. Trusting the
    // doc puts an elapsed 1000x too large on every line, and the duration is precisely what this
    // cluster's attributions turn on ("read the DURATION, then read whether it is a VALUE or a
    // BUDGET" — #2346). A wrong number is worse than none.
    //
    // The stamp is keyed by the test's UniqueID rather than held on this attribute instance: one
    // attribute instance serves every test, and MeshWeaver.Hosting.Orleans.Test runs
    // maxParallelThreads: 4, so an instance field would be a straight race between concurrent
    // tests. KeyValueStorage is one container for the whole pipeline (xUnit's own words), which is
    // why the key must carry the test id.
    private const string StartStampPrefix = "MeshWeaver.Fixture.AutoTestLogging.start:";

    /// <summary>
    /// Runs before each test method, writing a "TEST START" marker to the active file output helper.
    /// </summary>
    /// <param name="methodUnderTest">The test method that is about to run.</param>
    /// <param name="test">The xUnit test being executed.</param>
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        var className = methodUnderTest.DeclaringType?.Name ?? "UnknownTest";
        var testName = $"{className}.{methodUnderTest.Name}";
        var logMessage = $"=== TEST START: {testName} ===";


        // Also log to file output if available
        var fileOutput = XUnitFileOutputRegistry.GetAnyActiveOutputHelper();
        fileOutput?.SetCurrentTestMethod(methodUnderTest.Name);
        fileOutput?.WriteLine(logMessage);

        TestContext.Current.KeyValueStorage[StartStampPrefix + test.UniqueID] = Stopwatch.GetTimestamp();
        TestTraceLog.AppendPhase(className, "TEST_START", extra: methodUnderTest.Name);
    }

    /// <summary>
    /// Runs after each test method, writing any failure details and a "TEST END" marker
    /// to the active file output helper, then clearing the current test method.
    /// </summary>
    /// <param name="methodUnderTest">The test method that just ran.</param>
    /// <param name="test">The xUnit test that was executed.</param>
    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        // Also log to file output if available
        var fileOutput = XUnitFileOutputRegistry.GetAnyActiveOutputHelper();

        var state = TestContext.Current.TestState;

        if (state?.Result == TestResult.Failed)
        {
            var message = $"""=== TEST FAILED: {string.Join("\n", state.ExceptionMessages ?? [])}""";
            fileOutput?.WriteLine(message);
        }

        var className = methodUnderTest.DeclaringType?.Name ?? "UnknownTest";
        var testName = $"{className}.{methodUnderTest.Name}";
        var logMessage = $"=== TEST END: {testName} ===";


        fileOutput?.WriteLine(logMessage);
        fileOutput?.ClearCurrentTestMethod();

        TestTraceLog.AppendPhase(
            className,
            "TEST_END",
            ElapsedMs(test),
            $"{methodUnderTest.Name} outcome={OutcomeOf(state)}{FailureDetail(state)}");
    }

    private static long? ElapsedMs(IXunitTest test)
    {
        var key = StartStampPrefix + test.UniqueID;
        if (!TestContext.Current.KeyValueStorage.TryRemove(key, out var stamp) || stamp is not long started)
            return null;   // no stamp, no number — never a guessed one

        return (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    // The outcome is written even when xUnit could not produce a state, and it is written as
    // "Unknown" rather than omitted: a reader must be able to tell "this test ended without a
    // verdict" from "this test passed", and an absent field reads as neither.
    private static string OutcomeOf(TestResultState? state) =>
        state is null ? "Unknown" : state.Result.ToString();

    private static string? FailureDetail(TestResultState? state)
    {
        if (state is null || state.Result != TestResult.Failed)
            return null;

        var type = state.ExceptionTypes?.FirstOrDefault(t => t is not null) ?? "(no exception type)";
        var message = state.ExceptionMessages?.FirstOrDefault(m => m is not null) ?? "(no message)";

        // Newlines would break the one-row-per-test property the file is read with.
        var flattened = message.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        if (flattened.Length > MaxFailureChars)
            flattened = flattened[..MaxFailureChars] + $"… (+{message.Length - MaxFailureChars} chars, full text in the trx)";

        return $" :: {type}: {flattened}";
    }
}
