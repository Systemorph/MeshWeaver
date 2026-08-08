namespace MeshWeaver.Observability;

/// <summary>
/// The LogQL the watcher reads with. It lives here, beside <see cref="LogLineParser"/> and
/// <see cref="BurstAggregator"/>, because it is part of the same contract: those two reconstruct a
/// burst from consecutive lines, and that only works if the query actually returns those lines.
/// Keeping it in the watcher's own (executable) project put it out of reach of the tests — which is
/// how the defect below shipped.
/// </summary>
public static class LokiQuery
{
    /// <summary>
    /// Every line in a namespace — <b>deliberately unfiltered</b>.
    ///
    /// <para>🚨 This used to be <c>{namespace="x"} |~ `^(fail|crit):`</c>. A .NET console error is a
    /// red HEADER plus indented continuation lines (message, then stack trace), which Loki stores as
    /// separate entries; a line filter returns the header ALONE. The grouper then had nothing to
    /// re-attach, so every incident lost its exception and top frame and every fault in a category
    /// collapsed onto ONE fingerprint — tickets that named a component but not a defect. Found on
    /// the first production deploy (2026-08-08); the aggregator's unit tests never caught it because
    /// they feed it whole synthetic bursts, exercising a shape the real query could not produce.</para>
    ///
    /// <para>Red-detection therefore happens client-side in the grouper, where the burst is intact.
    /// The trade is volume — bounded by the caller's limit, with the cursor advancing only as far as
    /// was actually read, so an over-limit window is caught up rather than skipped.</para>
    /// </summary>
    /// <param name="ns">The Kubernetes namespace label to select.</param>
    /// <returns>The LogQL stream selector.</returns>
    public static string ForNamespace(string ns) => $"{{namespace=\"{ns}\"}}";
}
