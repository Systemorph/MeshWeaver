namespace MeshWeaver.Compiler;

/// <summary>
/// What comparing a stamped <see cref="CompiledDependencies"/> record against a live environment
/// ESTABLISHED — the distinction a <c>string?</c> "first mismatch" cannot carry.
///
/// <para>🚨 <b>"Not null" is not evidence of drift.</b> Before #3934 every non-match produced one
/// sentence and one action (rebuild), so "this environment has a DIFFERENT build of a module the
/// type binds" and "this environment cannot compare that entry at all" were indistinguishable in
/// the log, on the bake probe and on the status row. The measured cost is on
/// <c>Doc/Architecture/DependencyRecordFloor</c>: <c>SocialMedia/Post</c> served nothing for a
/// whole day while its NodeType read <c>compilationStatus: Ok</c>, and three investigations across
/// three repos could not tell an ordinary framework roll from a module drift.</para>
///
/// <para>Modelled deliberately on
/// <c>MeshWeaver.Mesh.Services.LanguageServer.NodeDiagnosticsOutcome</c> (#3912/#3916) rather than
/// inventing a second way to say "I could not check": one status, one sentence, and the
/// no-answer status can never read as the healthy one.</para>
/// </summary>
public enum DependencyRecordStatus
{
    /// <summary>
    /// Every entry was compared and every entry holds — an exact id still resolves identically,
    /// and every recorded FLOOR is met by this environment. The ONLY status that licenses adopting
    /// the stamped build.
    /// </summary>
    Satisfied,

    /// <summary>
    /// An entry was compared and does NOT hold: an exact id (a platform <c>ref:</c> surface, the
    /// reserved <c>!toolchain</c>/<c>!input</c> entries, a module pinned by build) resolves to
    /// something else here, or the name resolves to nothing at all. The bytes bind something this
    /// environment does not present.
    /// </summary>
    Drifted,

    /// <summary>
    /// A module entry recorded a FLOOR and this environment is BELOW it (#3934). Distinct from
    /// <see cref="Drifted"/> because the remedy is different and namable: the deployment is
    /// carrying an OLDER module than the build requires, so land a newer one — nothing about the
    /// platform surface moved.
    /// </summary>
    FloorNotMet,

    /// <summary>
    /// 🚨 NOTHING WAS CHECKED, and this is never evidence of health. Either the record is not
    /// trustworthy at all (no reserved <c>!toolchain</c> entry, so it could never invalidate), or a
    /// stamped entry and its live counterpart are expressed in schemes that cannot be compared —
    /// a recorded floor against an environment that can only report an MVID, or a legacy
    /// exact-build pin against an environment that reports a version. Every such case takes the
    /// REBUILD side, exactly as an inconclusive content key does: a false mismatch costs one
    /// rebuild, a false match serves stale bytes.
    /// </summary>
    NotChecked,
}

/// <summary>
/// The outcome of validating a stamped dependency record against one live environment:
/// <see cref="Status"/>, the entry that decided it, and the one-line sentence every log, decline
/// reason and status row prints.
///
/// <para><see cref="CompiledDependencies.FindMismatch"/> and
/// <see cref="CompiledDependencies.FindMismatchAfterReevaluation"/> are DERIVED from this — they
/// return <see cref="Problem"/> — so the string form and the status form can never disagree about
/// what a given record produces. Same idiom as <c>CheckSpeculative</c> over
/// <c>CheckSpeculativeOutcome</c> (#3888).</para>
/// </summary>
public sealed record DependencyRecordOutcome
{
    /// <summary>What the comparison established.</summary>
    public required DependencyRecordStatus Status { get; init; }

    /// <summary>The record key that decided the outcome — an assembly simple name, or one of the
    /// reserved <c>!</c> keys. Null for <see cref="DependencyRecordStatus.Satisfied"/> and for the
    /// untrusted-record case, which is a property of the record rather than of one entry.</summary>
    public string? Entry { get; init; }

    /// <summary>The one-line description, for logs and decline reasons. 🚨 Null EXACTLY when
    /// <see cref="Status"/> is <see cref="DependencyRecordStatus.Satisfied"/> — which is what lets
    /// the legacy string API be a projection of this record rather than a second
    /// implementation.</summary>
    public string? Problem { get; init; }

    /// <summary>True only for <see cref="DependencyRecordStatus.Satisfied"/>. 🚨 False for every
    /// other status INCLUDING <see cref="DependencyRecordStatus.NotChecked"/>, so a caller that
    /// reads only this flag still cannot mistake "no answer" for "compatible".</summary>
    public bool IsSatisfied => Status == DependencyRecordStatus.Satisfied;

    /// <summary>The satisfied outcome — the singleton, since it carries nothing (a constant, not a
    /// cache).</summary>
    public static DependencyRecordOutcome Satisfied { get; } =
        new() { Status = DependencyRecordStatus.Satisfied };

    /// <summary>An entry was compared and does not hold.</summary>
    /// <param name="entry">The record key.</param>
    /// <param name="problem">The sentence naming both sides.</param>
    public static DependencyRecordOutcome Drifted(string entry, string problem) =>
        new() { Status = DependencyRecordStatus.Drifted, Entry = entry, Problem = problem };

    /// <summary>A recorded floor is not met here.</summary>
    /// <param name="entry">The module's assembly simple name.</param>
    /// <param name="problem">The sentence naming the floor and what this environment has.</param>
    public static DependencyRecordOutcome FloorNotMet(string entry, string problem) =>
        new() { Status = DependencyRecordStatus.FloorNotMet, Entry = entry, Problem = problem };

    /// <summary>Nothing was checked — never a clean bill.</summary>
    /// <param name="entry">The record key, or null when the whole record is untrusted.</param>
    /// <param name="problem">The sentence saying what could not be compared.</param>
    public static DependencyRecordOutcome NotChecked(string? entry, string problem) =>
        new() { Status = DependencyRecordStatus.NotChecked, Entry = entry, Problem = problem };
}
