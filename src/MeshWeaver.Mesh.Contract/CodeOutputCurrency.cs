namespace MeshWeaver.Mesh;

/// <summary>
/// What an executable cell can HONESTLY say about the relationship between the output it is
/// showing and the code the reader is looking at.
///
/// <para>Four states, not two, because the honest answer to "is this output out of date?" is
/// sometimes <em>I cannot tell</em> — and collapsing that onto "no" is a claim, not an absence of
/// one. See <see cref="CodeOutputCurrencyExtensions.OutputCurrency"/>.</para>
/// </summary>
public enum CodeOutputCurrency
{
    /// <summary>
    /// The cell records no run at all. There is no output claim to be current or stale about, so
    /// the cell says nothing — a cell nobody has pressed Run on must not carry a warning.
    /// </summary>
    NeverRun,

    /// <summary>
    /// The cell PROVES its output belongs to the code on screen: it recorded a run, recorded the
    /// fingerprint of what that run submitted, and that fingerprint matches the current source.
    /// This is the only state that may be rendered as "up to date".
    /// </summary>
    Current,

    /// <summary>
    /// The cell proves the opposite: it recorded the fingerprint of its run, and the code has moved
    /// since. The visible output belongs to source the reader is no longer looking at — re-run.
    /// </summary>
    Stale,

    /// <summary>
    /// A run is recorded but its fingerprint is NOT — so the cell can neither prove nor disprove
    /// that the visible output belongs to the code on screen.
    ///
    /// <para>🚨 This is the fail-CLOSED state and the reason the verdict is not a <c>bool</c>. It
    /// arises whenever the last-execution stamp landed only in part: a node last executed by a build
    /// that predates <see cref="CodeConfiguration.LastExecutedCodeHash"/>, a merge patch written
    /// through a narrower shape, or any write that recorded the run without recording what it ran.
    /// Answering "not stale" here would assert a currency nothing substantiates — a WRONG claim
    /// rather than a missing one. Answering <see cref="Stale"/> would be equally dishonest (and
    /// would light every legacy node amber at once), so it gets its own state: say that the output
    /// is unverified, and let the reader decide whether to re-run.</para>
    /// </summary>
    Unverified,
}

/// <summary>
/// The single, fail-closed rule for "may this cell be shown as up to date?".
///
/// <para>The rule lives beside the field it interprets — <see cref="CodeConfiguration"/> and
/// <see cref="CodeFingerprint"/> are both here — rather than in whichever view happens to render a
/// cell, so every surface (the notebook toolbar, an agent reading a node, a future client) answers
/// the question the same way and inherits the fail-closed behaviour rather than re-deriving it.</para>
/// </summary>
public static class CodeOutputCurrencyExtensions
{
    /// <summary>
    /// What <paramref name="code"/> can honestly say about its own output.
    ///
    /// <para><b>The rule.</b> A cell may be shown as up to date only when it can PROVE it:
    /// it records a run, it records the <see cref="CodeFingerprint"/> of what that run submitted,
    /// and re-computing the fingerprint from the node's current
    /// <see cref="CodeConfiguration.Code"/> / <see cref="CodeConfiguration.Language"/> reproduces
    /// it. Anything less is <see cref="CodeOutputCurrency.Unverified"/>, never
    /// <see cref="CodeOutputCurrency.Current"/>.</para>
    ///
    /// <para><b>Why "a run is recorded" is deliberately generous.</b> The last-execution stamp
    /// writes <see cref="CodeConfiguration.LastExecutedAt"/>,
    /// <see cref="CodeConfiguration.LastExecutedBy"/>,
    /// <see cref="CodeConfiguration.LastActivityPath"/> and
    /// <see cref="CodeConfiguration.LastExecutedCodeHash"/> together, and any of them arriving
    /// without the hash means a run happened whose source we did not record. Requiring
    /// <c>LastExecutedAt</c> specifically would let a partial stamp that dropped only the timestamp
    /// fall back into <see cref="CodeOutputCurrency.NeverRun"/> — silence, on a cell that ran.</para>
    ///
    /// <para><b>And why an absent run is silent.</b> <see cref="CodeOutputCurrency.NeverRun"/> is
    /// not a weaker <see cref="CodeOutputCurrency.Unverified"/>: a cell nobody has run has no
    /// output to be wrong about, and warning there would cry wolf on every unrun cell in the
    /// mesh.</para>
    /// </summary>
    /// <param name="code">The cell's configuration; <c>null</c> reads as
    /// <see cref="CodeOutputCurrency.NeverRun"/> (there is no cell to judge).</param>
    public static CodeOutputCurrency OutputCurrency(this CodeConfiguration? code)
    {
        if (code is null)
            return CodeOutputCurrency.NeverRun;

        var ranAtLeastOnce = code.LastExecutedAt is not null
                             || !string.IsNullOrEmpty(code.LastActivityPath)
                             || !string.IsNullOrEmpty(code.LastExecutedBy);

        if (!ranAtLeastOnce)
            return CodeOutputCurrency.NeverRun;

        // A run without its fingerprint proves nothing about what it ran. Fail CLOSED.
        if (string.IsNullOrEmpty(code.LastExecutedCodeHash))
            return CodeOutputCurrency.Unverified;

        return CodeFingerprint.Of(code.Code, code.Language) == code.LastExecutedCodeHash
            ? CodeOutputCurrency.Current
            : CodeOutputCurrency.Stale;
    }

    /// <summary>
    /// Whether the cell may be rendered as "up to date" — true for
    /// <see cref="CodeOutputCurrency.Current"/> alone.
    ///
    /// <para>The predicate a view wants when it has one boolean to spend, written so that the
    /// unprovable cases fall on the safe side by construction: a caller cannot get the fail-closed
    /// behaviour wrong by forgetting to list a state.</para>
    /// </summary>
    /// <param name="code">The cell's configuration.</param>
    public static bool ProvesOutputIsCurrent(this CodeConfiguration? code) =>
        code.OutputCurrency() is CodeOutputCurrency.Current;
}
