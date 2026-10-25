using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// The verdicts a collection pass reports on its one <c>pass outcome</c> line. An OPEN set of string
/// constants (policy <c>open-vocabulary-string-constants</c>): a pass with a verdict of its own
/// declares its own constant next to it (the modules GC's <c>FAILED CLOSED</c>) and uses it in the
/// same field; nothing validates a verdict against this class.
/// </summary>
public static class PassOutcome
{
    /// <summary>Collection was armed and the pass ran its plan to the end.</summary>
    public const string Collected = "COLLECTED";

    /// <summary>Collection is not armed: the pass measured and planned, and deleted nothing.</summary>
    public const string ReportOnly = "REPORT ONLY";

    /// <summary>The pass refused to collect anything (an unreadable store, an unsafe plan).</summary>
    public const string Aborted = "ABORTED";

    /// <summary>
    /// The pool cancelled the pass (teardown) at a step boundary. Whatever it had removed by then
    /// is on the line; the rest of the plan is the next pass's.
    /// </summary>
    public const string Interrupted = "INTERRUPTED";

    /// <summary>The pass threw something other than the pool's cancel.</summary>
    public const string Faulted = "FAULTED";
}

/// <summary>
/// 🚨 <b>The one outcome a collection pass reports, and the step boundary at which a teardown ends
/// it.</b> Every pass that walks and prunes a shared volume (the prebuilt-bundle retention, the
/// assembly-cache retention, the modules GC) used to carry its own copy of the same two rules, and
/// only one of them had the second:
/// <list type="number">
/// <item><b>Exactly one Warning line per pass</b>, <c>{Pass}: pass outcome — {Outcome}…</c> (#6991).
/// The cluster ships only Warning and above to Loki, so this line is how anyone learns whether a
/// pass ran, what it removed and whether it refused. Itemized lines stay at Information.</item>
/// <item><b>A step boundary that observes the pool's token</b> (#7049). A pass runs as one blocking
/// leaf on the I/O pool, and <c>IoPool.Drain</c> waits for blocking leaves under the same budget as
/// everything else, so a pass that never looks at its token holds the silo's teardown join until it
/// finishes on its own. <see cref="Step"/> is that look: when the token is cancelled it records an
/// <see cref="PassOutcome.Interrupted"/> outcome (what the pass DID remove, via
/// <see cref="OnInterrupted"/>) and throws <see cref="OperationCanceledException"/>.</item>
/// </list>
///
/// <para><b>One outcome, enforced.</b> A second <see cref="Record(string, string, object?[])"/> on the same ledger throws: two
/// outcome lines for one pass would make the Loki count of passes wrong, and an interrupted pass
/// that also reported COLLECTED is precisely the contradiction this type exists to remove.</para>
///
/// <para><b>What it never decides</b>: what a pass collects. The plan is the pass's own; the ledger
/// only says how the pass ended. A cancel limits how much of the plan one pass reaches, never what
/// the plan is.</para>
///
/// <para>Not thread-safe by design: a pass is one blocking leaf, run on one thread from start to
/// end, and its ledger lives exactly as long as that leaf.</para>
/// </summary>
public sealed class PassLedger
{
    private readonly ILogger? logger;

    /// <summary>Creates the ledger of one pass.</summary>
    /// <param name="pass">The pass's name, the prefix of every line it writes
    /// (<c>AssemblyCacheRetention</c>, <c>PrebuiltBundleRetention</c>, <c>Modules GC</c>).</param>
    /// <param name="logger">Where the outcome line goes; null writes nothing and still enforces
    /// the step boundary.</param>
    public PassLedger(string pass, ILogger? logger)
    {
        Pass = pass;
        this.logger = logger;
    }

    /// <summary>The pass's name.</summary>
    public string Pass { get; }

    /// <summary>The outcome recorded so far, or null while the pass is still running.</summary>
    public string? Outcome { get; private set; }

    /// <summary>
    /// What an interruption reports: called once, by <see cref="Step"/>, before it throws. It
    /// should <see cref="Record(string, string, object?[])"/> an <see cref="PassOutcome.Interrupted"/> outcome carrying the
    /// pass's progress (and may append a ledger file of its own). When it records nothing, or none
    /// is set, the ledger records a generic one, so an interrupted pass never goes unreported.
    /// </summary>
    public Action? OnInterrupted { get; set; }

    /// <summary>
    /// Record the pass's one outcome at Warning: <c>{Pass}: pass outcome — {Outcome}</c> followed by
    /// <paramref name="detail"/>, a message template whose holes <paramref name="args"/> fill.
    /// </summary>
    /// <param name="outcome">A <see cref="PassOutcome"/> constant, or a pass's own.</param>
    /// <param name="detail">The rest of the line, starting with its own separator
    /// (<c>": removed {Removed} …"</c>, <c>" (collection is not armed), …"</c>).</param>
    /// <param name="args">The template's arguments.</param>
    /// <exception cref="InvalidOperationException">An outcome was already recorded for this pass.</exception>
    public void Record(string outcome, string detail, params object?[] args) =>
        Record(outcome, null, detail, args);

    /// <summary><see cref="Record(string, string, object?[])"/>, with the exception that decided it.</summary>
    /// <param name="outcome">A <see cref="PassOutcome"/> constant, or a pass's own.</param>
    /// <param name="exception">The exception the line carries.</param>
    /// <param name="detail">The rest of the line.</param>
    /// <param name="args">The template's arguments.</param>
    /// <exception cref="InvalidOperationException">An outcome was already recorded for this pass.</exception>
    public void Record(string outcome, Exception? exception, string detail, params object?[] args)
    {
        if (Outcome is not null)
            throw new InvalidOperationException(
                $"{Pass}: a second pass outcome ({outcome}) after {Outcome} — one pass reports one outcome");
        Outcome = outcome;
        logger?.Log(LogLevel.Warning, exception, "{Pass}: pass outcome — {Outcome}" + detail,
            [Pass, outcome, .. args]);
    }

    /// <summary>
    /// A step boundary. Returns at once while <paramref name="cancellationToken"/> is live; once it
    /// is cancelled, records the interruption (see <see cref="OnInterrupted"/>) and throws
    /// <see cref="OperationCanceledException"/>. Call it before every round trip a pass makes to the
    /// volume it prunes.
    /// </summary>
    /// <param name="cancellationToken">The pool's token, as the leaf received it.</param>
    public void Step(CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
            return;
        Interrupt(cancellationToken);
    }

    /// <summary>
    /// The pool cancelled the pass: record the interruption (once) and throw. For a
    /// <c>catch (OperationCanceledException) when (ct.IsCancellationRequested)</c> around a step the
    /// pass delegated (a walk that threw from inside), so that cancel is reported exactly like one
    /// <see cref="Step"/> caught.
    /// </summary>
    /// <param name="cancellationToken">The pool's token; it must be cancelled.</param>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    public void Interrupt(CancellationToken cancellationToken)
    {
        if (Outcome is null)
        {
            OnInterrupted?.Invoke();
            if (Outcome is null)
                Record(PassOutcome.Interrupted,
                    ": the pool cancelled the pass (teardown) before it reported its progress; the next pass plans it again");
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException($"{Pass}: Interrupt was called with a token that is not cancelled");
    }

    /// <summary>
    /// How a pass's wrapper (a hosted service subscribing to the pooled pass) reports the pass's
    /// terminal error: the pool's cancel is a teardown, not a fault — the pass has already recorded
    /// its <see cref="PassOutcome.Interrupted"/> line, which a <c>FAULTED … nothing was collected</c>
    /// would contradict — so it goes to Information; anything else is the pass's
    /// <see cref="PassOutcome.Faulted"/> outcome at Warning.
    /// </summary>
    /// <param name="pass">The pass's name.</param>
    /// <param name="logger">Where the line goes.</param>
    /// <param name="error">The pass's terminal error.</param>
    /// <param name="faultDetail">The FAULTED line after its outcome (<c>": the sweep of {Root} faulted …"</c>).</param>
    /// <param name="args">The template's arguments.</param>
    /// <returns>True when the error was the pool's cancel.</returns>
    public static bool ReportTerminal(string pass, ILogger? logger, Exception error, string faultDetail, params object?[] args)
    {
        if (error is OperationCanceledException)
        {
            logger?.LogInformation(
                "{Pass}: the pass was cancelled by the pool (teardown); its INTERRUPTED outcome says what it "
                + "removed, and the next pass plans the rest", pass);
            return true;
        }
        new PassLedger(pass, logger).Record(PassOutcome.Faulted, error, faultDetail, args);
        return false;
    }
}
