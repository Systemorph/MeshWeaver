using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Services;

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
    /// <para><b>Why every field is treated as evidence of a run.</b> The last-execution stamp writes
    /// <see cref="CodeConfiguration.LastExecutedAt"/>, <see cref="CodeConfiguration.LastExecutedBy"/>,
    /// <see cref="CodeConfiguration.LastActivityPath"/> and
    /// <see cref="CodeConfiguration.LastExecutedCodeHash"/> together, so ANY of them present means a
    /// run happened. Requiring <c>LastExecutedAt</c> specifically would let a partial stamp that
    /// dropped only the timestamp fall back into <see cref="CodeOutputCurrency.NeverRun"/> —
    /// silence, on a cell that ran.</para>
    ///
    /// <para><b>And why the fingerprint is tested FIRST.</b> It is both evidence of a run and the
    /// only field that can decide currency, so a node carrying the hash and nothing else is fully
    /// determinable. Checking the other three first would answer
    /// <see cref="CodeOutputCurrency.NeverRun"/> there and silence a verdict we can actually
    /// substantiate — the same fail-open shape as the defect this rule exists to remove, mirrored.
    /// (Found by review on the PR that introduced this rule.)</para>
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

        // The fingerprint is BOTH evidence that a run happened and the only field that can decide
        // currency, so whenever it is present the verdict is the comparison — whatever else the
        // stamp failed to land. Testing the run markers first would answer NeverRun for a node
        // carrying only the hash and silence an indicator that is fully determinable: the same
        // fail-open shape, mirrored.
        if (!string.IsNullOrEmpty(code.LastExecutedCodeHash))
            return CodeFingerprint.Of(code.Code, code.Language) == code.LastExecutedCodeHash
                ? CodeOutputCurrency.Current
                : CodeOutputCurrency.Stale;

        // No fingerprint: nothing here can prove what ran. If anything else says a run happened,
        // fail CLOSED rather than claim currency; otherwise the cell genuinely has nothing to show.
        var ranAtLeastOnce = code.LastExecutedAt is not null
                             || !string.IsNullOrEmpty(code.LastActivityPath)
                             || !string.IsNullOrEmpty(code.LastExecutedBy);

        return ranAtLeastOnce
            ? CodeOutputCurrency.Unverified
            : CodeOutputCurrency.NeverRun;
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

/// <summary>
/// The half of the verdict that does NOT depend on the last-execution stamp (#3301).
///
/// <para><b>The gap this closes.</b> A cell reads its own run history from ONE denormalised
/// pointer — the stamp that writes <see cref="CodeConfiguration.LastExecutedAt"/>,
/// <see cref="CodeConfiguration.LastExecutedBy"/>, <see cref="CodeConfiguration.LastActivityPath"/>
/// and <see cref="CodeConfiguration.LastExecutedCodeHash"/> together. When that write does not land
/// (#3249: the workspace not ready, a refused partition write, content that cannot be read as a
/// <see cref="CodeConfiguration"/>) the pointer is simply absent, and after a page reload the cell
/// is indistinguishable from one nobody has ever run. <see cref="CodeOutputCurrencyExtensions.OutputCurrency"/>
/// answers <see cref="CodeOutputCurrency.NeverRun"/> — correctly, from the evidence it has — and the
/// reader is told the run never happened.</para>
///
/// <para><b>Why a READ is the fix and a write is not.</b> The run is not lost. The dispatcher
/// creates the Activity node BEFORE it dispatches and stamps the originating cell onto it
/// (<c>ActivityLog.HubPath</c> = the Code node's path), so the cell → run edge is durable; it is
/// only missing in the direction the view reads it. Every scheme that recovers it by putting a
/// marker on the CELL is a second write to the node whose first write just failed — recovery in the
/// same failure domain — which is why <c>Doc/Architecture/ScriptExecution</c> → "When the stamp does
/// not land" rules all three out. Following the edge backwards needs no write at all.</para>
///
/// <para><b>Why this is a legitimate query.</b> It is a LISTING BY PREDICATE, where a stale negative
/// is harmless: the worst outcome is the answer the cell already gives today. It is emphatically not
/// the forbidden shape — reading one known node's content by path — which stays
/// <c>GetMeshNodeStream</c>'s job.</para>
///
/// <para><b>And why it is not run on every cell.</b> <see cref="ResolveOutputCurrency"/> consults
/// the mesh ONLY where the stamp answers <see cref="CodeOutputCurrency.NeverRun"/> — the one state
/// where the lookup can change the outcome. A notebook of cells that ran normally costs zero
/// queries.</para>
/// </summary>
public static class CodeRunHistory
{
    /// <summary>
    /// The <c>nodeType</c> a run's Activity node carries. Spelled as a literal because
    /// <c>MeshWeaver.Mesh.Contract</c> sits BELOW <c>MeshWeaver.Graph.Contract</c>, where
    /// <c>GraphNodeTypeNames.Activity</c> declares it — the two are pinned equal by
    /// <c>CodeRunHistoryTest.TheActivityNodeTypeNameMatchesTheGraphVocabulary</c> rather than left to
    /// agree by luck.
    /// </summary>
    public const string ActivityNodeTypeName = "Activity";

    /// <summary>
    /// The sentinel a Code node (or a <c>PartitionDefinition</c>) may put in
    /// <see cref="CodeConfiguration.ActivityParentPath"/> to mean "the VIEWER's home partition, not
    /// mine" — expanded by <c>CodeNodeType.ResolveActivityParent</c> at dispatch, and expanded the
    /// same way here so the lookup searches where the run actually landed.
    /// </summary>
    public const string ViewerHomeSentinel = "{viewer}";

    /// <summary>
    /// The one spelling of "this cell's own runs": Activity satellites in
    /// <paramref name="activityNamespace"/> whose <c>ActivityLog.HubPath</c> is
    /// <paramref name="cellPath"/>.
    ///
    /// <para>Ordered newest-first and capped at one row: the question is EXISTENCE, so listing every
    /// run of a cell that has run a thousand times would pay for nine hundred and ninety-nine rows
    /// nobody reads.</para>
    /// </summary>
    /// <param name="cellPath">Path of the Code node — the value the dispatcher wrote to <c>HubPath</c>.</param>
    /// <param name="activityNamespace">The <c>{parent}/_Activity</c> namespace to look in.</param>
    public static string RunsQuery(string cellPath, string activityNamespace) =>
        $"namespace:{activityNamespace} nodeType:{ActivityNodeTypeName} "
        + $"content.hubPath:{cellPath} sort:LastModified-desc limit:1";

    /// <summary>
    /// The <c>_Activity</c> namespaces a cell's runs can land in, derived from the data at hand —
    /// the cell's own <see cref="CodeConfiguration.ActivityParentPath"/> (with
    /// <see cref="ViewerHomeSentinel"/> expanded), its partition root (the default), and the
    /// viewer's home. Deduplicated, so the common case is a single namespace.
    ///
    /// <para>🚨 <b>What this deliberately does NOT cover, stated rather than hidden.</b> A
    /// <c>PartitionDefinition.DefaultActivityParentPath</c> pointing at a FOURTH place is invisible
    /// here: that lookup is a live workspace query on the partition registry, which this assembly
    /// sits below. Where it applies, the lookup finds nothing and the verdict falls back to
    /// <see cref="CodeOutputCurrency.NeverRun"/> — exactly what the cell says today, so the gap
    /// costs nothing that was already working. A caller that has ALREADY resolved the parent (the
    /// dispatcher does, through <c>CodeNodeType.ResolveActivityParent</c>) skips this derivation and
    /// calls <see cref="RunsQuery"/> with the namespace it resolved.</para>
    /// </summary>
    /// <param name="cellPath">Path of the Code node.</param>
    /// <param name="code">The cell's configuration, for its activity-parent override.</param>
    /// <param name="viewerHome">The reading viewer's home partition, if known.</param>
    public static ImmutableArray<string> ActivityNamespaces(
        string? cellPath, CodeConfiguration? code, string? viewerHome)
    {
        if (string.IsNullOrEmpty(cellPath))
            return [];

        var partitionRoot = cellPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(partitionRoot))
            return [];

        var home = string.IsNullOrEmpty(viewerHome) ? null : viewerHome;
        var configured = code?.ActivityParentPath switch
        {
            null or "" => null,
            ViewerHomeSentinel => home ?? partitionRoot,
            var p => p,
        };

        return new[] { configured, partitionRoot, home }
            .Where(parent => !string.IsNullOrEmpty(parent))
            .Select(parent => $"{parent}/{ActivityNodeGuard.ActivitySegment}")
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
    }

    /// <summary>
    /// The cell's currency verdict, recovered from the mesh when its own stamp cannot supply one.
    ///
    /// <list type="number">
    ///   <item><see cref="CodeOutputCurrencyExtensions.OutputCurrency"/> first. Anything but
    ///   <see cref="CodeOutputCurrency.NeverRun"/> is returned unchanged and costs NO query — the
    ///   stamp is the fast path and stays it.</item>
    ///   <item>Only on <see cref="CodeOutputCurrency.NeverRun"/>: one listing over the cell's
    ///   candidate <c>_Activity</c> namespaces for a run that names this cell. A hit means a run
    ///   happened; nothing about that run says WHAT it ran, so the honest verdict is
    ///   <see cref="CodeOutputCurrency.Unverified"/> — the same fail-closed answer as a stamp that
    ///   landed without its fingerprint, and for the same reason.</item>
    ///   <item>No hit ⇒ <see cref="CodeOutputCurrency.NeverRun"/>, unchanged. A cell nobody has run
    ///   still says nothing, and a viewer who cannot SEE the activity (the query is access-filtered
    ///   like every other) gets the same silence rather than a claim about data they may not read.</item>
    /// </list>
    ///
    /// <para><b>One-shot by construction.</b> The result is the query's <c>Initial</c> snapshot and
    /// then completion — this is a read, not a binding. A live subscription per never-run cell is
    /// the cost the issue's own analysis rules out, and a stale negative here is harmless by the
    /// same argument that makes the listing legitimate at all. A view keeps its liveness from the
    /// node stream it is already bound to: when a later run stamps the cell, the node emits and the
    /// caller re-resolves.</para>
    /// </summary>
    /// <param name="code">The cell's configuration.</param>
    /// <param name="cellPath">Path of the Code node.</param>
    /// <param name="viewerHome">The reading viewer's home partition, if known.</param>
    /// <param name="meshService">The mesh's query surface.</param>
    /// <returns>Exactly one verdict, then completion.</returns>
    public static IObservable<CodeOutputCurrency> ResolveOutputCurrency(
        this CodeConfiguration? code,
        string? cellPath,
        string? viewerHome,
        IMeshService meshService)
    {
        var stamped = code.OutputCurrency();
        if (stamped is not CodeOutputCurrency.NeverRun)
            return Observable.Return(stamped);

        var namespaces = ActivityNamespaces(cellPath, code, viewerHome);
        if (namespaces.Length == 0)
            return Observable.Return(CodeOutputCurrency.NeverRun);

        return meshService
            .Query<MeshNode>(MeshQueryRequest.FromQueries(
                namespaces.Select(ns => RunsQuery(cellPath!, ns))))
            .Where(change => change.ChangeType is QueryChangeType.Initial)
            .Take(1)
            .Select(change => change.Items.Count > 0
                ? CodeOutputCurrency.Unverified
                : CodeOutputCurrency.NeverRun);
    }
}
