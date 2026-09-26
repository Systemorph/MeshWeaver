namespace Memex.Portal.ServiceDefaults;

/// <summary>
/// 🚨 <b>The three probe endpoints, and the ONE rule that keeps them apart: liveness and readiness
/// ask different questions, so they must never share a path</b> (MeshWeaver#3330).
///
/// <para><b>The questions.</b>
/// <list type="bullet">
/// <item><see cref="Health"/> — <i>"is everything this portal needs actually up?"</i> Every
/// registered check, tagged or not: the database, the mesh, the NodeType bake gate. Heavy. It is
/// the <c>startupProbe</c>, and nothing else, because it is the only probe Kubernetes suspends the
/// other two behind.</item>
/// <item><see cref="Live"/> — <i>"am I making progress?"</i> Only checks tagged
/// <see cref="LiveTag"/>. Failing it means <b>restart me</b>, so the bar is a condition a restart
/// actually fixes — a process spending most of its wall clock in GC pauses is the canonical one
/// (<c>ProcessProgressHealthCheck</c>, in MeshWeaver.Plugins).</item>
/// <item><see cref="Ready"/> — <i>"can I take a request?"</i> Only checks tagged
/// <see cref="ReadyTag"/>. Failing it means <b>send my traffic to my siblings</b>, which is a
/// claim about the siblings as much as about this pod, and therefore a much rarer thing to be
/// able to say honestly.</item>
/// </list></para>
///
/// <para>🚨 <b>Why <see cref="Ready"/> exists at all — the defect it removes.</b> Until #3330 the
/// chart pointed BOTH post-startup probes at <see cref="Live"/>. That was safe only for as long as
/// <see cref="Live"/> stayed the trivial process-up check it shipped as: nothing carried
/// <see cref="LiveTag"/>, so the predicate matched nothing and answered 200 for any process that
/// could accept a socket. <c>MeshWeaver.Plugins#1234</c> then tagged a progress-aware check —
/// correct, and the fix to a real blindness — and readiness silently inherited it, because two
/// probes cannot be given different SEMANTICS while they share a PATH.</para>
///
/// <para>The result was an amplifier built out of the containment: readiness trips at
/// 10 s × 3 = 30 s, liveness at 15 s × 6 = 90 s, so a GC-bound replica left the Service a full
/// minute before anything restarted it, and for that minute its traffic landed on siblings
/// converging on the SAME memory ceiling (measured 2026-09-04 in ns <c>memex</c>: two 28 h
/// replicas at 9936Mi and 9409Mi — ratio 1.06). One sick replica became a cascade. That is the
/// 2026-07-21 death spiral, which the chart's readiness comment has warned against ever since,
/// rebuilt out of the #2194 item-4 fix.</para>
///
/// <para>🚨 <b>Why the readiness predicate is an ALLOW-list.</b> The bug was not a wrong number, it
/// was a check registered in ANOTHER repository joining readiness's predicate by carrying a tag
/// chosen for liveness. A deny-list (<c>!Tags.Contains(LiveTag)</c>) would repeat that in the
/// worse direction: every UNtagged check — the database and mesh checks <see cref="Health"/> is
/// made of — would join readiness by default, which is precisely the heavy-readiness death spiral
/// of 2026-07-21. With <see cref="ReadyTag"/> as its own opt-in, a host that wants to leave the
/// Service must say so in exactly those words.</para>
///
/// <para><b>Where the paths are consumed.</b> The chart
/// (<c>deploy/helm/templates/memex-portal/deployment.yaml</c>) names them in its three probes; the
/// setup-only host answers all of them while an instance awaits configuration
/// (<c>SetupOnlyHost.ProbePaths</c>). <c>ProbeSemanticsGuard</c> holds the chart and this file to
/// each other, and <c>ProbeSeparationTest</c> drives both post-startup paths over real HTTP with a
/// failing liveness check to prove they can still answer differently.</para>
/// </summary>
public static class ProbeEndpoints
{
    /// <summary>
    /// <c>/health</c> — every registered check. The <c>startupProbe</c>'s path, and nothing else's:
    /// as a post-startup probe it is the 2026-07-21 death spiral (a heavy check times out under
    /// load, the pod is yanked from the Service, and the survivors inherit its traffic).
    ///
    /// <para>🚨 Its HTTP status is the STARTUP verdict — "did the process boot" — and a check
    /// tagged <see cref="RollGateTag"/> never contributes to it. Such a check is still RUN here and
    /// its reading always PRINTS in the body, so the instrument an operator reads is unchanged; it
    /// just cannot fail the one probe whose failure kills the container.</para>
    /// </summary>
    public const string Health = "/health";

    /// <summary>
    /// <c>/alive</c> — the LIVENESS path. Checks tagged <see cref="LiveTag"/> only. The
    /// <c>livenessProbe</c>'s path, and never the <c>readinessProbe</c>'s.
    /// </summary>
    public const string Live = "/alive";

    /// <summary>
    /// <c>/ready</c> — the READINESS path. Checks tagged <see cref="ReadyTag"/> or
    /// <see cref="RollGateTag"/>: the trivial process-up check, plus any roll gate the host
    /// registered (the NodeType bake gate, <c>nodetype_bake</c>).
    /// </summary>
    public const string Ready = "/ready";

    /// <summary>
    /// Puts a check on <see cref="Live"/>. Tag one only when failing it should make Kubernetes
    /// RESTART the pod.
    /// </summary>
    public const string LiveTag = "live";

    /// <summary>
    /// Puts a check on <see cref="Ready"/>. Tag one only when failing it should make Kubernetes
    /// take this pod OUT OF ROTATION and give its traffic to its siblings — which is a statement
    /// about the siblings' spare capacity, not only about this pod.
    /// </summary>
    public const string ReadyTag = "ready";

    /// <summary>
    /// 🚨 <b>Makes a check's reading PRINT on <see cref="Health"/> even when it is Healthy — the
    /// one thing that lets an operator tell "I measured nothing" from "I measured, and it was
    /// clean"</b> (MeshWeaver#3703, #3704).
    ///
    /// <para><c>WriteHealthWithDetail</c> prints only entries that are NOT Healthy, which is right
    /// for a VERDICT — nobody wants a wall of green — and wrong for a CENSUS, where the number IS
    /// the publication. A census check that answered Healthy-and-silent would be indistinguishable
    /// from one that was never registered, and that ambiguity is precisely what left #3703 and
    /// #3704 unanswerable: their readings existed only in a boot log, and log access on this fleet
    /// is break-glass.</para>
    ///
    /// <para>🚨 It is NOT a probe tag. <see cref="Live"/> and <see cref="Ready"/> filter on their
    /// own tags, so a census check reaches neither: publishing a number can never restart a pod or
    /// take it out of rotation. Tag a check with this ONLY when a reader needs its reading whether
    /// or not the reading is a problem — and keep the STATUS meaning what it always meant, so the
    /// aggregate word on line one does not turn Degraded for a clean census.</para>
    /// </summary>
    public const string CensusTag = "census";

    /// <summary>
    /// 🚨 <b>A ROLL GATE: the check holds READINESS only — it never fails the startup probe and
    /// never restarts anything</b> (policy <c>bake-gate-readiness-only</c>, MeshWeaver#5544).
    ///
    /// <para>A roll gate answers "may this image take traffic here?", which is a question about the
    /// ROLL, not about whether the process booted. The only safe consequence of "no" is that the
    /// pod stays alive and out of the Service, so the roll stalls with the previous image serving.
    /// On the startup probe the same "no" KILLS the container once
    /// <c>periodSeconds × failureThreshold</c> runs out, and a restarted pod of the PREVIOUS image
    /// must pass the same probe: that is how the <c>nodetype_bake</c> verdict took
    /// memex.systemorph.com down from 20:54Z to 04:07Z on 2026-09-25/26 (three-hour container
    /// deaths, Doc/Architecture/TheBakeGateOnlyStallsARoll).</para>
    ///
    /// <para>So a check carrying this tag is read by <see cref="Ready"/> (it joins that endpoint's
    /// allow-list) and is excluded from <see cref="Health"/>'s STATUS, while its reading still
    /// prints in <see cref="Health"/>'s body whatever it says. It must not also carry
    /// <see cref="LiveTag"/>. <c>ServiceDefaults.RollGateChecks</c> applies it by name to the
    /// NodeType bake gate, so the rule holds for a host that registered that check untagged.</para>
    /// </summary>
    public const string RollGateTag = "roll-gate";
}
