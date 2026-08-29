using System.Runtime.CompilerServices;

namespace MeshWeaver.Fixture;

/// <summary>
/// Process-wide observer for the two BENIGN test-teardown races, so an unobserved task exception
/// of either shape is marked observed rather than left to whatever policy the host has. This
/// module initializer runs once per test-process for EVERY project that references
/// <c>MeshWeaver.Fixture</c> (i.e. all of them via the test base classes) — the
/// <c>MeshWeaver.Hosting.Orleans.Test</c>-local suppressor it replaced protected only one project.
///
/// <para>🚨 <b>This class does NOT stop a "Catastrophic failure", and this doc used to say it did.</b>
/// The claim ("escape as unobserved task exceptions and get escalated by xUnit v3") is wrong, and it
/// is the kind of wrong that costs an afternoon: it names the wrong EVENT, so anyone chasing a
/// green-but-exit-1 shard starts by reading this file and concludes the suppressor "ran too late".
/// Measured twice, 2026-08-29 — by disassembling the shipped runners, and by two probes inside
/// <c>MeshWeaver.AI.Test</c>:</para>
/// <list type="bullet">
///   <item><b>xunit.v3 3.2.2 never hooks <see cref="TaskScheduler.UnobservedTaskException"/></b> —
///   zero references in the shipped binaries — and .NET Core does not escalate unobserved task
///   exceptions (<c>ThrowUnobservedTaskExceptions</c> is set nowhere in either repo). A deliberately
///   unobserved <see cref="ObjectDisposedException"/> of the disposed-<c>LifetimeScope</c> shape
///   makes a suite exit <b>0</b> and print nothing at all.</item>
///   <item>The channel that reds a green shard is <see cref="AppDomain.UnhandledException"/>, hooked
///   by <c>Xunit.Runner.InProc.SystemConsole.ConsoleRunner</c>, which writes
///   <c>ErrorMessage.FromException</c> — <b>message only, no stack</b> — then lets the run finish and
///   exits 2. A raw <c>new Thread(… throw …)</c> reproduces the whole signature exactly:
///   <c>Passed! - Failed: 0</c> AND exit 1.</item>
/// </list>
/// <para>So this class is inert against that failure mode by construction, and cannot be "fixed" to
/// cover it — <c>SetObserved()</c> has nothing to say about an unhandled thread exception. The
/// class that DOES cover it is <see cref="TeardownStragglerCapturer"/> in this same assembly, whose
/// own doc reached this conclusion independently; it records the full stack the runner omits. It is
/// kept rather than deleted because observing a benign unobserved exception is still correct
/// hygiene and is cheap, and because a future runner may reinstate the escalation.</para>
///
/// <para>The two benign races, both "a message/continuation runs AFTER the test's
/// scope is gone":</para>
/// <list type="number">
///   <item><b>Disposed Autofac <c>LifetimeScope</c></b> — when a <c>TestCluster</c>
///   / mesh is disposed, an in-flight message is still being (de)serialized on a
///   ThreadPool task; resolving a codec from the now-disposed container throws
///   <see cref="ObjectDisposedException"/> ("LifetimeScope … already disposed").</item>
///   <item><b>"There is no currently active test"</b> — a background hub
///   continuation (logger write, observable OnNext) runs after the test method
///   returned and touches xUnit's per-test <c>TestContext</c>, which throws
///   <see cref="InvalidOperationException"/>.</item>
/// </list>
///
/// <para>We observe the unobserved exception ONLY when EVERY inner exception is one
/// of these two benign shapes — any other unobserved exception is left untouched
/// and still surfaces as a real failure. This is the same conservative contract as
/// the original Orleans-only suppressor; it is widened in scope (all projects) and
/// in the exception set (adds the "no active test" race), not loosened.</para>
/// </summary>
internal static class TestTeardownExceptionSuppressor
{
    [ModuleInitializer]
    public static void Init()
    {
        TaskScheduler.UnobservedTaskException += static (_, e) =>
        {
            var inners = e.Exception?.Flatten().InnerExceptions;
            if (inners is { Count: > 0 } && inners.All(IsBenignTeardownException))
                e.SetObserved();
        };
    }

    internal static bool IsBenignTeardownException(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;

        // (1) disposed Autofac LifetimeScope during in-flight (de)serialization
        if (ex is ObjectDisposedException
            && msg.Contains("LifetimeScope", StringComparison.OrdinalIgnoreCase))
            return true;
        if (msg.Contains("nested lifetimes cannot be created", StringComparison.OrdinalIgnoreCase))
            return true;

        // (2) xUnit "no currently active test" — a continuation ran after teardown
        if (ex is InvalidOperationException
            && msg.Contains("no currently active test", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
