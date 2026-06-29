using System.Runtime.CompilerServices;

namespace MeshWeaver.Fixture;

/// <summary>
/// Process-wide suppressor for the two BENIGN test-teardown races that otherwise
/// escape as unobserved task exceptions and get escalated by xUnit v3 to a
/// "Catastrophic failure" — which aborts the whole test assembly/collection
/// (no per-test trx, every test in it reported failed) even though each test
/// passes in isolation. This module initializer runs once per test-process for
/// EVERY project that references <c>MeshWeaver.Fixture</c> (i.e. all of them via
/// the test base classes), so the suppression is no longer Orleans-only — the
/// <c>MeshWeaver.Hosting.Orleans.Test</c>-local suppressor only protected one
/// project, leaving e.g. Security.Test to abort under CI load.
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
