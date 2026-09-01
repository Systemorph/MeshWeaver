namespace MeshWeaver.Testing;

/// <summary>
/// The per-case context a build-run test sees — the shape xUnit's <c>TestContext.Current</c>
/// gave the suites this replaces, so a migrated body keeps reading
/// <c>TestContext.Current.CancellationToken</c>. The runner sets it on the case's own thread
/// before invoking the method; the token trips at the case budget, so a case that threads it
/// through its waits ends with a named timeout instead of a hung build.
///
/// <para>🚨 <b>This is the STATIC lane's surface and nothing else.</b> It exists because
/// <c>mw-plugin-test build</c> executes a NodeType's <c>Test/*.cs</c> with no xUnit anywhere in the
/// process — <see cref="MeshWeaver.PluginTester.StaticTestRunner"/> reflects over the emitted
/// assembly and invokes the methods itself. It is deliberately NOT a second xUnit: no fixtures, no
/// attributes, no theories, no assertion library. The mesh lane, which has a real host, is free to
/// run real xUnit; nothing here is meant to serve it.</para>
///
/// <para>🚨 <b>Thread affinity is the whole design.</b> The cascade builds packages in PARALLEL and
/// every case gets its own thread, so anything shared process-wide is a data race between two
/// unrelated cases. The context is <c>[ThreadStatic]</c> and the case's log sink hangs off the
/// context (<see cref="TestContextData.Log"/>) rather than off a static — which is why
/// <see cref="TestLog.Sink"/> is only ever the fallback for a write made when NO case is
/// running.</para>
/// </summary>
public static class TestContext
{
    [ThreadStatic] private static TestContextData? current;

    /// <summary>The running case's context; a neutral one when no case is running.</summary>
    public static TestContextData Current => current ??= new TestContextData("(no case)", CancellationToken.None);

    /// <summary>Installs the context for the case about to run on this thread.</summary>
    public static IDisposable Enter(string caseName, CancellationToken token) =>
        Enter(new TestContextData(caseName, token));

    /// <summary>
    /// Installs <paramref name="data"/> as this thread's context and restores the previous one on
    /// dispose.
    ///
    /// <para>🚨 <b>Enter and Dispose must happen on the SAME thread.</b> The context is
    /// <c>[ThreadStatic]</c>, so a dispose from another thread would clear THAT thread's context —
    /// silently unsetting a different, concurrently running case. Rather than corrupt a neighbour,
    /// a cross-thread dispose is a no-op; the owning thread's slot is overwritten by its next
    /// <see cref="Enter(TestContextData)"/> and dies with the thread.</para>
    /// </summary>
    public static IDisposable Enter(TestContextData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var previous = current;
        var owner = Environment.CurrentManagedThreadId;
        current = data;
        return new Restore(() =>
        {
            if (Environment.CurrentManagedThreadId == owner)
                current = previous;
        });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

/// <summary>What a case may ask about itself.</summary>
/// <param name="CaseName"><c>Type.Method</c> of the running case.</param>
/// <param name="CancellationToken">Trips at the case budget.</param>
public sealed record TestContextData(string CaseName, CancellationToken CancellationToken)
{
    /// <summary>
    /// Where this case's <see cref="TestLog"/> lines go. The runner installs a per-case collector
    /// so the lines can be ATTACHED to the case's result instead of being smeared across the build
    /// log of whichever package happened to be writing at the same moment. Null outside a run.
    /// </summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Thrown by a case that decides it cannot run here (a platform it is not on, a credential it
/// does not have). The runner reports it as <c>skipped</c> — never as passed, never as failed.
/// </summary>
public sealed class SkipException(string reason) : Exception(reason)
{
    /// <summary>The reason the case gave.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// Where a case writes what it wants a reader to see. Lines are captured against the running case
/// and printed with its failure, so a failure's neighbourhood is readable without a debugger —
/// the role <c>ITestOutputHelper</c> played in the suites this replaces.
/// </summary>
public static class TestLog
{
    /// <summary>
    /// Process-wide fallback for a line written when NO case is running; the console otherwise.
    ///
    /// <para>🚨 Not the per-case sink, and it must never become one: one static shared by every
    /// concurrently building package would attribute one case's output to another. The runner
    /// installs <see cref="TestContextData.Log"/> instead, which is reached through the
    /// <c>[ThreadStatic]</c> context and so belongs to exactly one case.</para>
    /// </summary>
    public static Action<string>? Sink { get; set; }

    /// <summary>Writes one line, prefixed with the running case.</summary>
    public static void WriteLine(string message)
    {
        var context = TestContext.Current;
        var line = $"      [{context.CaseName}] {message}";
        (context.Log ?? Sink ?? Console.WriteLine)(line);
    }

    /// <summary>Writes one formatted line.</summary>
    public static void WriteLine(string format, params object?[] args) =>
        WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));
}
