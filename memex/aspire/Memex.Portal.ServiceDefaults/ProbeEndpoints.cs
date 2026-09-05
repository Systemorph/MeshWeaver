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
/// probes cannot be given different SEMANTICS while they share a PATH.
///
/// <para>The result was an amplifier built out of the containment: readiness trips at
/// 10 s × 3 = 30 s, liveness at 15 s × 6 = 90 s, so a GC-bound replica left the Service a full
/// minute before anything restarted it, and for that minute its traffic landed on siblings
/// converging on the SAME memory ceiling (measured 2026-09-04 in ns <c>memex</c>: two 28 h
/// replicas at 9936Mi and 9409Mi — ratio 1.06). One sick replica became a cascade. That is the
/// 2026-07-21 death spiral, which the chart's readiness comment has warned against ever since,
/// rebuilt out of the #2194 item-4 fix.</para></para>
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
    /// </summary>
    public const string Health = "/health";

    /// <summary>
    /// <c>/alive</c> — the LIVENESS path. Checks tagged <see cref="LiveTag"/> only. The
    /// <c>livenessProbe</c>'s path, and never the <c>readinessProbe</c>'s.
    /// </summary>
    public const string Live = "/alive";

    /// <summary>
    /// <c>/ready</c> — the READINESS path. Checks tagged <see cref="ReadyTag"/> only, which today
    /// is the trivial process-up check and deliberately nothing else.
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
}
