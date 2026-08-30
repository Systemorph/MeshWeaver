namespace MeshWeaver.Testing;

/// <summary>
/// The per-case context a build-run test sees — the shape xUnit's <c>TestContext.Current</c>
/// gave the suites this replaces, so a migrated body keeps reading
/// <c>TestContext.Current.CancellationToken</c>. The runner sets it on the case's own thread
/// before invoking the method; the token trips at the case budget, so a case that threads it
/// through its waits ends with a named timeout instead of a hung build.
/// </summary>
public static class TestContext
{
    [ThreadStatic] private static TestContextData? current;

    /// <summary>The running case's context; a neutral one when no case is running.</summary>
    public static TestContextData Current => current ??= new TestContextData("(no case)", CancellationToken.None);

    /// <summary>Installs the context for the case about to run on this thread.</summary>
    public static IDisposable Enter(string caseName, CancellationToken token)
    {
        var previous = current;
        current = new TestContextData(caseName, token);
        return new Restore(() => current = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

/// <summary>What a case may ask about itself.</summary>
/// <param name="CaseName"><c>Type.Method</c> of the running case.</param>
/// <param name="CancellationToken">Trips at the case budget.</param>
public sealed record TestContextData(string CaseName, CancellationToken CancellationToken);

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
/// Where a case writes what it wants a reader to see. Lines go to the runner's output (the build
/// log) prefixed with the case name, so a failure's neighbourhood is readable without a debugger —
/// the role <c>ITestOutputHelper</c> played in the suites this replaces.
/// </summary>
public static class TestLog
{
    /// <summary>Optional sink the runner installs; the console otherwise.</summary>
    public static Action<string>? Sink { get; set; }

    /// <summary>Writes one line, prefixed with the running case.</summary>
    public static void WriteLine(string message)
    {
        var line = $"      [{TestContext.Current.CaseName}] {message}";
        (Sink ?? Console.WriteLine)(line);
    }

    /// <summary>Writes one formatted line.</summary>
    public static void WriteLine(string format, params object?[] args) =>
        WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));
}
