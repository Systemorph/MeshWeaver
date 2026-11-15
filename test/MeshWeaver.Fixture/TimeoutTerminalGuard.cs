using System.Collections.Immutable;
using System.Reflection;
using MeshWeaver.Messaging;

namespace MeshWeaver.Fixture;

/// <summary>
/// 🚨 <b>Timeout == error.</b> Arms the timeout-terminal guard for every test in the assembly or
/// class it is placed on: a test during which a product-side timeout terminal FIRED
/// (<see cref="TimeoutTerminalKinds"/> — a hub <c>RequestTimeout</c>, a query fan-in that did not
/// emit an Initial, a write or create verdict deadline) FAILS, unless the test declares that
/// terminal with <see cref="ExpectsTimeoutTerminalAttribute"/>.
///
/// <para>Why: a test that passes only because the product recovered from a stall through its own
/// timeout is a stall nobody fixed — it costs the suite the whole bound on every run and hides the
/// defect behind a green tick. Setting <c>MW_TIMEOUT_TERMINALS_ARE_ERRORS=1</c> arms the guard for
/// EVERY test, which is how the remaining offenders are enumerated. Unarmed, an undeclared terminal
/// is still written to the test output and the shard trace as <c>TIMEOUT_TERMINAL</c>, so it is
/// visible, but it does not fail the test.</para>
///
/// <para>Full reference: <c>Doc/Architecture/TimeoutIsError</c>.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class TimeoutTerminalsAreErrorsAttribute : Attribute;

/// <summary>
/// Declares that the test (method) or every test in the class EXPECTS a product-side timeout
/// terminal of <see cref="Kind"/> to fire — because the timeout IS the behaviour under test. A
/// declared terminal that never fires fails the armed test too: a stale declaration is an
/// exemption nobody needs, and it would hide the next real stall of that kind.
///
/// <para>Declare it only where the timeout is the subject. Never declare it to make a slow test
/// pass — fix the stall instead.</para>
/// </summary>
/// <param name="kind">The <see cref="TimeoutTerminalKinds"/> value (or a module's own constant).</param>
/// <param name="because">Why the timeout is the behaviour under test.</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class ExpectsTimeoutTerminalAttribute(string kind, string because) : Attribute
{
    /// <summary>The expected terminal kind.</summary>
    public string Kind { get; } = kind;

    /// <summary>Why the timeout is the behaviour under test.</summary>
    public string Because { get; } = because;
}

/// <summary>
/// The <see cref="ITimeoutTerminalObserver"/> the test harness registers in every test's container
/// — an instance owned by that container, never static. It only records; the verdict is
/// <see cref="TimeoutTerminalVerdict"/>'s.
/// </summary>
public sealed class TimeoutTerminalRecorder : ITimeoutTerminalObserver
{
    private ImmutableList<TimeoutTerminal> fired = ImmutableList<TimeoutTerminal>.Empty;

    /// <summary>Every terminal recorded so far, in arrival order.</summary>
    public ImmutableList<TimeoutTerminal> Fired => Volatile.Read(ref fired);

    /// <inheritdoc />
    public void OnTimeoutTerminal(TimeoutTerminal terminal)
        => ImmutableInterlocked.Update(ref fired, list => list.Add(terminal));
}

/// <summary>
/// Something a test holds — a cluster fixture, a cluster host — that owns containers OTHER than the
/// test's own, each with its own <see cref="TimeoutTerminalRecorder"/>: an Orleans test cluster's
/// silos and client host build their own containers, so a terminal that fires on a silo never
/// reaches the test container's recorder. <c>TestBase</c> finds every instance field of the test
/// that holds one and folds its recorders into the test's window, so the guard sees those
/// terminals too instead of silently under-reporting there.
/// </summary>
public interface ITimeoutTerminalSource
{
    /// <summary>The recorders of the containers this source owns, as they exist now.</summary>
    IEnumerable<TimeoutTerminalRecorder> TimeoutTerminalRecorders { get; }
}

/// <summary>
/// Decides whether the terminals that fired during one test are a failure.
/// </summary>
public static class TimeoutTerminalVerdict
{
    /// <summary>The environment variable that arms the guard for every test.</summary>
    public const string ArmEverywhereVariable = "MW_TIMEOUT_TERMINALS_ARE_ERRORS";

    /// <summary>True when the guard is armed for <paramref name="method"/>.</summary>
    /// <param name="method">The test method.</param>
    public static bool IsArmed(MethodInfo method)
    {
        if (Environment.GetEnvironmentVariable(ArmEverywhereVariable) is "1" or "true" or "True")
            return true;
        var type = method.ReflectedType ?? method.DeclaringType;
        return type?.GetCustomAttribute<TimeoutTerminalsAreErrorsAttribute>(inherit: true) is not null
               || type?.Assembly.GetCustomAttribute<TimeoutTerminalsAreErrorsAttribute>() is not null;
    }

    /// <summary>The terminal kinds <paramref name="method"/> declares, from the method and its class.</summary>
    /// <param name="method">The test method.</param>
    public static ImmutableHashSet<string> DeclaredKinds(MethodInfo method)
    {
        var type = method.ReflectedType ?? method.DeclaringType;
        return method.GetCustomAttributes<ExpectsTimeoutTerminalAttribute>(inherit: true)
            .Concat(type?.GetCustomAttributes<ExpectsTimeoutTerminalAttribute>(inherit: true) ?? [])
            .Select(a => a.Kind)
            .ToImmutableHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The failure message for one test, or <c>null</c> when the terminals that fired are exactly
    /// what it declared. Every undeclared terminal is named with its origin and message; every
    /// declared kind that did not fire is named as stale.
    /// </summary>
    /// <param name="testName">The test's display name.</param>
    /// <param name="fired">The terminals that fired during the test.</param>
    /// <param name="declared">The kinds the test declares.</param>
    public static string? Evaluate(string testName, IReadOnlyList<TimeoutTerminal> fired, ImmutableHashSet<string> declared)
    {
        var undeclared = fired.Where(t => !declared.Contains(t.Kind)).ToList();
        var stale = declared.Where(k => fired.All(t => t.Kind != k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (undeclared.Count == 0 && stale.Count == 0)
            return null;
        var lines = new List<string>
        {
            $"Timeout == error: {testName} — a product-side timeout terminal decided this test's outcome."
        };
        lines.AddRange(undeclared.Select(t =>
            $"  UNDECLARED {t.Kind} at '{t.Origin}': {Clamp(t.Message)}"));
        lines.AddRange(stale.Select(k =>
            $"  DECLARED-BUT-NOT-FIRED {k}: the test declares [ExpectsTimeoutTerminal(\"{k}\")] and no such terminal fired — remove the stale declaration."));
        if (undeclared.Count > 0)
            lines.Add("  Fix the stall so the event arrives without the timeout, or drive the timeout on virtual "
                      + "time and declare it with [ExpectsTimeoutTerminal] if the timeout IS the behaviour under test. "
                      + "Never widen the bound.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string Clamp(string message)
    {
        var oneLine = message.ReplaceLineEndings(" ");
        return oneLine.Length <= 600 ? oneLine : oneLine[..600] + "…";
    }
}

/// <summary>
/// Thrown from the test's <c>After</c> hook when an armed test's timeout terminals do not match its
/// declaration. xUnit reports an exception from <c>After</c> as the test's failure.
/// </summary>
/// <param name="message">The verdict.</param>
public sealed class UndeclaredTimeoutTerminalException(string message) : Exception(message);
