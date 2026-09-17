using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;

namespace MeshWeaver.Hosting;

/// <summary>
/// One bake report this replica actually produced, reduced to the scalars a <c>/health</c> line can
/// carry. Deliberately NOT the <c>NodeTypeBakeReport</c> itself: the host-side check that prints
/// this must not need the compiler pipeline's types to read a census.
/// </summary>
/// <param name="Pass">Which pass produced it — the adopt-only probe, or the compiling sweep.</param>
/// <param name="FrameworkVersion">The live framework identity the probe resolved against.</param>
/// <param name="Total">NodeTypes in the report's population.</param>
/// <param name="Baked">Types whose record and the share agree — a count over RECORDS, not a store census.</param>
/// <param name="Pending">Types the report says still need building.</param>
/// <param name="ClassifiedFromLocalAdoption">
/// #3703's verdict: how many entries were classified from a definition THIS process wrote rather
/// than from the enumeration snapshot, because the snapshot's node version proves it predates that
/// write. Zero on every steady-state boot; non-zero means the sweep's input was behind.
/// </param>
/// <param name="AdoptionStamps">
/// How many NodeTypes this process stamped from a prebuilt adoption, at the moment the report was
/// recorded. The OTHER half of the 78-vs-5 pair: a different population in different units from
/// <paramref name="Baked"/>, published beside it so the two can never again be read as a
/// contradiction (#3703).
/// </param>
/// <param name="Summary">The report's own one-line summary — the same string the log carries.</param>
/// <param name="At">When this reading was recorded.</param>
public sealed record BakeReportReading(
    string Pass,
    string FrameworkVersion,
    int Total,
    int Baked,
    int Pending,
    int ClassifiedFromLocalAdoption,
    int AdoptionStamps,
    string Summary,
    DateTimeOffset At)
{
    /// <summary>
    /// 🚨 <b>WHOSE the non-baked types are — the identity <see cref="Summary"/>'s counts drop</b>
    /// (#4258).
    ///
    /// <para><c>NodeTypeBakeReport.Ownership</c>, carried across the one call that used to reduce the
    /// report to scalars. <c>previouslybroken=1</c> is the only record in the whole system that a
    /// NodeType is permanently broken — the rollout gate skips it on purpose — and a count nobody can
    /// resolve to an owner is not an answer: it is why #3883 failed to close three times on the
    /// ambiguity between "the type is gone" and "I may not read it".</para>
    ///
    /// <para>🚨 It names the PARTITION each non-baked type lives in and stops there
    /// (<c>BinaryClickerV2/…</c>), because this is published on a PUBLIC, unauthenticated body. That
    /// routes a finding to an owner without disclosing a node title — the surface #3890 closed a
    /// narrower version of.</para>
    ///
    /// <para>An init-only property rather than a primary-constructor parameter, deliberately: a
    /// record's arity is its binary contract here (<c>scripts/check-record-signatures.py</c>), and a
    /// tenth parameter would abort every host serving a module compiled against the ninth.</para>
    /// </summary>
    public string Ownership { get; init; } = string.Empty;

    /// <summary>
    /// 🚨 <b>THE OUTCOME, as opposed to the PLAN</b> (#4645) — how the sweep SETTLED on this
    /// replica, or the empty string when no sweep has reported at all.
    ///
    /// <para>Every other number on this reading describes what the bake INTENDED: how many types
    /// the share already covers, how many are left to build. None of them answers the question an
    /// operator staring at an empty page actually has — <i>is there a NodeType this replica cannot
    /// serve?</i> A plan is not an outcome, and a replica can publish a perfectly clean plan and
    /// then fail to compile half of it.</para>
    ///
    /// <para>One of <c>completed</c>, <c>faulted</c>, <c>not applicable</c> (adoption ran, no bake
    /// did), or empty. Empty is NOT "clean": it is "this replica has measured nothing about what it
    /// can serve", and <see cref="NodeTypeBakeReportRegistry.Describe"/> says so in those words.</para>
    /// </summary>
    public string SweepSettlement { get; init; } = string.Empty;

    /// <summary>
    /// 🚨 <b>THE DENOMINATOR of the outcome census.</b> How many NodeTypes REPORTED a terminal
    /// outcome on this replica — which is not the same as how many were DECIDED: the unknown and
    /// the withdrawn are inside this number, and only <see cref="UsableHere"/> plus
    /// <see cref="NoUsableAssembly"/> are verdicts. Calling it "reached a verdict" is what made the
    /// printed denominator contradict itself. Read every other outcome count against this one and against
    /// <see cref="Total"/>: a zero in <see cref="NoUsableAssembly"/> means "nothing to see" only
    /// when this number accounts for the population, and means "I could not look" otherwise.
    /// </summary>
    public int OutcomesReached { get; init; }

    /// <summary>
    /// Types that ended the sweep with a usable assembly on this replica — compiled here, or found
    /// already on the shared store (<c>PreWarmOutcome.ReachedUsableBuild</c>). The only outcome
    /// that needs nothing from anybody.
    /// </summary>
    public int UsableHere { get; init; }

    /// <summary>
    /// 🚨 <b>THE VERDICT THAT IS NEVER BENIGN: types this replica has NO usable assembly for.</b>
    ///
    /// <para>A compile that failed, or was skipped because an upstream's did, or whose declared
    /// sources no longer resolve. Whatever the cause, the consequence on this replica is the same
    /// and it is visible to users: the type's content cannot be typed, its layout areas are never
    /// registered, and every page that asks for one renders the area-not-found frame. That is the
    /// symptom this census exists to make findable BEFORE somebody reports a blank page.</para>
    ///
    /// <para>Deliberately EXCLUDES <c>Retired</c> and <c>Removed</c> — a type whose repository
    /// withdrew it is not a defect on this replica, and counting it here would make the census cry
    /// wolf on every completed retirement, which is precisely how a detector stops being read.
    /// Those are counted in <see cref="Withdrawn"/> so the denominator still adds up.</para>
    /// </summary>
    public int NoUsableAssembly { get; init; }

    /// <summary>
    /// Types whose verdict is "I did not find out" — the sweep timed out on them, or on something
    /// they depend on, or its own subscription faulted. 🚨 Counted APART from both usable and
    /// unusable, because folding an unknown into either direction is the defect this whole census
    /// is built against: an unmeasured type is not a healthy one and is not a broken one.
    /// </summary>
    public int Unknown { get; init; }

    /// <summary>
    /// Types their own repository has withdrawn — <c>Retired</c> (sources deliberately pulled) or
    /// <c>Removed</c> (the definition node is gone). Not a defect and not a pass: printed so that
    /// <see cref="OutcomesReached"/> reconciles and a reader can see that a non-zero unusable count
    /// is not just a retirement wave.
    /// </summary>
    public int Withdrawn { get; init; }

    /// <summary>
    /// The unusable types' verdicts, as <c>&lt;status&gt;×N in &lt;partition&gt;</c>. 🚨 The
    /// PARTITION only, never the node's own title — this lands on a PUBLIC, unauthenticated body,
    /// and a partition routes a finding to an owner without disclosing a node name (#4258, #3890).
    /// </summary>
    public string OutcomeDetail { get; init; } = string.Empty;
}

/// <summary>
/// 🚨 <b>The bake verdict, PUBLISHED — so it can be read with <c>curl</c> instead of Loki</b>
/// (MeshWeaver#3703).
///
/// <para>#3703 is blocked on nothing about its own mechanism. Its confirming reading — how many of
/// this boot's bake classifications came from a record this process had already written, against
/// how many prebuilt adoptions it had stamped — existed ONLY as a boot log line, and log access on
/// this fleet is break-glass. So the issue could be neither confirmed nor closed by anything an
/// operator is authorised to run. This registry holds the last reading in process; the host's
/// <c>BakeReportHealthCheck</c> prints it on <c>ProbeEndpoints.Health</c>, which is public,
/// unauthenticated and past RLS.</para>
///
/// <para>🚨 <b>A missing report may not read as a clean one.</b> <c>/health</c> prints only entries
/// that are NOT Healthy, so a check that answered Healthy-and-silent when it had nothing would be
/// indistinguishable from one that was never registered — the exact ambiguity #3703 and #3704 are
/// stuck in. <see cref="Describe"/> therefore has a sentence for "no report" and
/// <see cref="IsClean"/> refuses to call it clean, and the check that reads them carries
/// <c>ProbeEndpoints.CensusTag</c> so its CLEAN reading prints too, numbers and all.</para>
///
/// <para>A mesh-scoped instance singleton (registered in <c>AddMeshCatalog</c>, beside
/// <see cref="ContentDegradationRegistry"/>), never static — its lifetime is the mesh's. One
/// reading, overwritten: the question is "what does THIS replica's bake say", not a history.</para>
/// </summary>
public sealed class NodeTypeBakeReportRegistry
{
    /// <summary>The adopt-only pass every boot runs — it asks the store and builds nothing.</summary>
    public const string AdoptOnlyProbe = "adopt-only probe";

    /// <summary>The compiling sweep — only when <c>PreWarm:DynamicTypes</c> is on.</summary>
    public const string CompilingSweep = "compiling sweep";

    /// <summary>The check's name on <c>/health</c>.</summary>
    public const string HealthCheckName = "bake-report";

    /// <summary>
    /// Volatile reference, not a gate: one writer per pass, any number of probe readers, and a
    /// torn read is impossible for a reference. No lock, no semaphore — there is nothing to
    /// serialise.
    /// </summary>
    private volatile BakeReportReading? latest;

    /// <summary>
    /// The terminal verdict each NodeType reached on THIS replica, keyed by type path. Bounded by
    /// construction — one entry per type, the last verdict winning (a retraction after a recovery
    /// watch fires is a later verdict, not a second one). An instance field on a mesh-scoped
    /// singleton, never static.
    /// </summary>
    private readonly ConcurrentDictionary<string, PreWarmStatus> outcomes = new(StringComparer.Ordinal);

    private volatile string settlement = string.Empty;

    /// <summary>Records the reading this pass produced, replacing any earlier one.</summary>
    /// <param name="reading">The reading.</param>
    public void Record(BakeReportReading reading) => latest = Project(reading);

    /// <summary>
    /// 🚨 <b>Records ONE type's terminal verdict — the outcome census's only input</b> (#4645).
    ///
    /// <para>Called from the sweep's subscriber, beside the readiness gate's own
    /// <c>MarkOutcome</c>, on the verdict that subscriber is already holding. So this costs NO
    /// extra I/O, cannot storm (one entry per type per sweep) and cannot disagree with the gate:
    /// both read the same object. It is deliberately NOT a second probe — a census that re-asked
    /// the store would be a different measurement wearing the sweep's name.</para>
    /// </summary>
    /// <param name="outcome">The type's terminal outcome.</param>
    public void RecordOutcome(PreWarmOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        outcomes[outcome.TypePath] = outcome.Status;
        latest = Project(latest);
    }

    /// <summary>
    /// Records how the sweep SETTLED — <c>completed</c>, <c>faulted</c>, or <c>not applicable</c>
    /// (adoption ran and no bake did). 🚨 The empty default is load-bearing: it is what makes "no
    /// sweep reported here" a different printed sentence from "a sweep reported and found nothing
    /// wrong", which is the whole reason this census exists rather than a counter.
    /// </summary>
    /// <param name="value">The settlement word.</param>
    public void RecordSettlement(string value)
    {
        settlement = value ?? string.Empty;
        latest = Project(latest);
    }

    /// <summary>
    /// Folds the outcome census onto a reading. Pure given this registry's state, so the reading
    /// the host prints always carries the newest verdicts without the host knowing there are two
    /// halves.
    /// </summary>
    /// <param name="reading">The reading to fold onto, or <c>null</c>.</param>
    /// <returns>The reading with the census folded in, or <c>null</c>.</returns>
    private BakeReportReading? Project(BakeReportReading? reading)
    {
        if (reading is null)
            return null;
        var all = outcomes.ToArray();
        var unusable = all
            .Where(pair => Verdict(pair.Value) == OutcomeKind.NoAssembly)
            .ToArray();
        return reading with
        {
            SweepSettlement = settlement,
            OutcomesReached = all.Length,
            UsableHere = all.Count(pair => Verdict(pair.Value) == OutcomeKind.Usable),
            NoUsableAssembly = unusable.Length,
            Unknown = all.Count(pair => Verdict(pair.Value) == OutcomeKind.Unknown),
            Withdrawn = all.Count(pair => Verdict(pair.Value) == OutcomeKind.Withdrawn),
            OutcomeDetail = string.Join(
                "; ",
                unusable
                    .GroupBy(pair => (Status: pair.Value, Partition: PartitionOf(pair.Key)))
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key.Partition, StringComparer.Ordinal)
                    .Select(group => $"{group.Key.Status}×{group.Count()} in {group.Key.Partition}")),
        };
    }

    /// <summary>How one verdict counts in the census.</summary>
    private enum OutcomeKind
    {
        /// <summary>A usable assembly is on this replica.</summary>
        Usable,

        /// <summary>No usable assembly — the never-benign verdict.</summary>
        NoAssembly,

        /// <summary>The sweep did not find out.</summary>
        Unknown,

        /// <summary>The repository withdrew the type.</summary>
        Withdrawn,
    }

    /// <summary>
    /// 🚨 The classification, as one pure function so a test can hold every member of
    /// <see cref="PreWarmStatus"/> against it. The default arm is <c>NoAssembly</c>
    /// DELIBERATELY: a status added later and not classified here must show up as a problem to be
    /// looked at, never quietly join the healthy count — an unclassified outcome reading as a pass
    /// is the exact failure this census is built against.
    /// </summary>
    /// <param name="status">The terminal status.</param>
    /// <returns>How it counts.</returns>
    private static OutcomeKind Verdict(PreWarmStatus status) => status switch
    {
        PreWarmStatus.Compiled or PreWarmStatus.AlreadyBaked => OutcomeKind.Usable,
        PreWarmStatus.TimedOut or PreWarmStatus.UpstreamUnevaluated or PreWarmStatus.Faulted
            => OutcomeKind.Unknown,
        PreWarmStatus.Retired or PreWarmStatus.Removed => OutcomeKind.Withdrawn,
        _ => OutcomeKind.NoAssembly,
    };

    /// <summary>
    /// The partition a type path lives in — its first segment, and nothing else, because this
    /// reaches a public body.
    /// </summary>
    /// <param name="typePath">The NodeType's mesh path.</param>
    /// <returns>The partition.</returns>
    private static string PartitionOf(string typePath)
    {
        if (string.IsNullOrEmpty(typePath))
            return "(unknown)";
        var slash = typePath.IndexOf('/');
        return slash > 0 ? typePath[..slash] : typePath;
    }

    /// <summary>The last reading, or <c>null</c> when this replica has produced none.</summary>
    public BakeReportReading? Latest => latest;

    /// <summary>
    /// 🚨 Whether the reading is a CLEAN verdict. <c>null</c> is never clean: there is no report to
    /// be clean, and an instrument that reports nothing may not read as a pass.
    /// </summary>
    /// <param name="reading">The reading, or <c>null</c>.</param>
    /// <returns><c>true</c> only for a reading whose snapshot was not behind this process.</returns>
    public static bool IsClean(BakeReportReading? reading) =>
        reading is not null && reading.ClassifiedFromLocalAdoption == 0;

    /// <summary>
    /// The one sentence an operator reads on <c>/health</c>. Pure — it is the whole publication, so
    /// a test can pin it without a host.
    /// </summary>
    /// <param name="reading">The reading, or <c>null</c> when none was produced.</param>
    /// <returns>The sentence.</returns>
    public static string Describe(BakeReportReading? reading)
    {
        if (reading is null)
            return "NO bake report on this replica — nothing published one, so this replica has "
                   + "measured NOTHING about which of its NodeTypes the share already holds, and "
                   + "NOTHING about which of them it can actually serve. This is an absence of "
                   + "measurement, NOT a clean bake and NOT a clean replica (#3703, #4645).";

        // 🚨 #4258 — the counts were the WHOLE publication, and a count nobody can resolve to an
        // owner cannot close anything. The partition each non-baked type lives in is printed here;
        // the node's own title deliberately is not, because this body is public.
        var ownership = string.IsNullOrEmpty(reading.Ownership)
            ? string.Empty
            : $" Non-baked types by partition: {reading.Ownership} (the PARTITION each one lives in "
              + "— this body is public and unauthenticated, so the node's own name is deliberately "
              + "not printed; a partition routes the finding to an owner, #4258).";

        var common =
            $"{reading.Pass} at {reading.At.ToString("O", CultureInfo.InvariantCulture)}: "
            + $"{reading.Summary}.{ownership} Adoption stamps held by this process: {reading.AdoptionStamps} "
            + "(a count of NodeTypes THIS process stamped from a prebuilt adoption — a different "
            + $"population, in different units, from the {reading.Baked} whose record and the share "
            + "agree; the two are not comparable and never were, #3703).";

        var plan = reading.ClassifiedFromLocalAdoption == 0
            ? common
            : $"the NodeType enumeration snapshot PREDATED this replica's own prebuilt adoptions "
              + $"for {reading.ClassifiedFromLocalAdoption} of {reading.Total} type(s), which were "
              + "therefore classified from the record this process wrote rather than from the "
              + $"snapshot (#3703). {common}";

        return $"{plan} {DescribeOutcomes(reading)}";
    }

    /// <summary>
    /// 🚨 <b>THE OUTCOME CENSUS's one sentence — the post-bytes-win verdict</b> (#4645).
    ///
    /// <para>Everything above it describes the PLAN: what the share holds, what is left to build.
    /// This says what actually HAPPENED on this replica, which is the only half that answers the
    /// question a blank page raises. Pure, so a test can pin every branch without a host.</para>
    ///
    /// <para>🚨 <b>It prints a reading whatever the status, and the three readings are three
    /// different sentences.</b> "No sweep has reported here", "a sweep reported and every type it
    /// reached is servable", and "a sweep reported and N types are not" must never be confusable,
    /// and an absent outcome may never read as a clean one — that ambiguity is the defect class
    /// this census belongs to, not a corner of it.</para>
    ///
    /// <para>🚨 <b>And it always states its DENOMINATOR.</b> A zero in the unusable count
    /// means "nothing to see" only against the number of types that reached a verdict AND the
    /// number enumerated; without both, a zero is equally "I could not look".</para>
    /// </summary>
    /// <param name="reading">The reading.</param>
    /// <returns>The sentence.</returns>
    internal static string DescribeOutcomes(BakeReportReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        if (reading.OutcomesReached == 0 && string.IsNullOrEmpty(reading.SweepSettlement))
            return "OUTCOME CENSUS: NO sweep has reported an outcome on this replica — the "
                   + "compiling sweep is off, or has not settled, so nothing here says whether any "
                   + "NodeType is unservable HERE. The counts above are a PLAN, not an outcome. "
                   + "This is an absence of measurement, NOT a clean replica (#4645).";

        var settled = string.IsNullOrEmpty(reading.SweepSettlement)
            ? "not settled"
            : reading.SweepSettlement;
        // 🚨 OutcomesReached INCLUDES the unknown and the withdrawn — it counts types that
        // REPORTED something, not types that were decided. Saying "reached a verdict" here and
        // then "N reached none" in the same breath was a contradiction in the one sentence whose
        // whole job is to be readable, and it would teach a reader to distrust the numbers.
        var residue =
            $" Denominator: {reading.OutcomesReached} of {reading.Total} enumerated type(s) "
            + $"reported an outcome; of those, {reading.Unknown} reached NO verdict (timed out, "
            + $"or waiting on something that did) and {reading.Withdrawn} were withdrawn by "
            + "their own repository (retired or removed — counted apart on purpose, so a "
            + "retirement wave cannot be read as breakage). The remaining "
            + $"{reading.Total - reading.OutcomesReached} enumerated type(s) reported nothing "
            + "at all.";

        if (reading.NoUsableAssembly == 0)
            return $"OUTCOME CENSUS (sweep {settled}): every one of the {reading.UsableHere} type(s) "
                   + "that reached a verdict has a usable assembly on this replica."
                   + residue;

        return $"🚨 OUTCOME CENSUS (sweep {settled}): {reading.NoUsableAssembly} NodeType(s) "
               + "have NO usable assembly on this replica — their content cannot be typed here and "
               + "their layout areas are never registered, so every page that asks for one renders "
               + $"the area-not-found frame: {reading.OutcomeDetail} (the PARTITION each one lives "
               + "in — this body is public and unauthenticated, so the node's own name is "
               + "deliberately not printed; a partition routes the finding to an owner, #4258)."
               + residue;
    }
}
