using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// How far this process has got towards being a legitimate PARTICIPANT in the mesh — as opposed to
/// merely being a running process that can read it.
///
/// <para>🚨 <b>Readiness gates TRAFFIC. This gates MEMBERSHIP.</b> Issue #3478: on
/// <c>memex.systemorph.com</c>, 2026-09-06, the NodeType bake readiness gate correctly refused an
/// image (<c>ci.7926</c>, one regressed NodeType) and Kubernetes correctly held the rollout with the
/// previous image serving — and the refused pod then ran for two hours INSIDE the mesh, stamping
/// NodeType compile records with its own framework identity. Two images lived in one mesh,
/// <c>Crm/Offer</c> and <c>Crm/Opportunity</c> adopted assemblies stamped for an identity the
/// serving replicas could not load, and every deal and offer page on the client portal was dead
/// (#3472). The gate's verdict was right both times; what was wrong is that a refused process was
/// still a participant.</para>
/// </summary>
public enum MeshAdmission
{
    /// <summary>
    /// Nothing arms this process's admission — no <see cref="IMeshAdmissionAuthority"/> is
    /// registered, or every registered one says it is not armed. Publications pass straight
    /// through, exactly as they did before the gate existed.
    ///
    /// <para>🚨 The fail-OPEN default, and deliberately so: enforcement is opt-in, exactly like the
    /// readiness gate it mirrors. A deployment that never armed a validator has nothing to validate
    /// against, and a gate that black-holed such a process would turn a configuration default into
    /// an outage. "Registered" and "armed" stay separate.</para>
    /// </summary>
    Unarmed,

    /// <summary>
    /// Armed, and the verdict does not exist yet — the validation is still running, or has not
    /// started. The process may read the mesh and compile; it may NOT publish. Publications are
    /// HELD (never dropped) and released the moment the verdict is <see cref="Admitted"/>.
    ///
    /// <para>This is "a provisional membership that cannot stamp a generation". It is what makes
    /// <i>validate before running</i> implementable at all: the bake sweep has to read NodeTypes
    /// through the mesh to have anything to validate, so membership-for-reading cannot be moved
    /// after the verdict — but membership-for-WRITING can, and that is the half that does the
    /// damage.</para>
    /// </summary>
    Provisional,

    /// <summary>The validation passed. This process is a full participant; publications run.</summary>
    Admitted,

    /// <summary>
    /// The validation FAILED. Publications are refused and dropped, and held ones are discarded —
    /// this process must leave no trace of its identity in the shared mesh.
    ///
    /// <para>Level-triggered, not latched: it is re-read at every publication, so a process that
    /// becomes unhealthy AFTER being admitted stops publishing from that moment, and one whose
    /// verdict is retracted resumes. See <see cref="MeshPublicationGate.Publish"/>.</para>
    /// </summary>
    Refused,
}

/// <summary>
/// Something that can say whether this process has passed ITS validation — the input side of
/// <see cref="MeshPublicationGate"/>.
///
/// <para>Several may be registered; the gate takes the MOST RESTRICTIVE answer
/// (<see cref="MeshAdmission.Refused"/> &gt; <see cref="MeshAdmission.Provisional"/> &gt;
/// <see cref="MeshAdmission.Admitted"/> &gt; <see cref="MeshAdmission.Unarmed"/>), because an
/// authority that refuses is stating evidence and an authority that admits is only stating the
/// absence of it.</para>
///
/// <para>The one implementation today is <c>MeshWeaver.Hosting.NodeTypeBakeGateState</c>, whose
/// <c>Admission</c> is derived from the SAME predicate as its readiness verdict, so "not ready" and
/// "not admitted" cannot drift apart.</para>
/// </summary>
public interface IMeshAdmissionAuthority
{
    /// <summary>This authority's current verdict. Read on every publication — never cached.</summary>
    MeshAdmission Admission { get; }

    /// <summary>Why, for the log line a refusal or a release writes.</summary>
    string AdmissionReason { get; }
}

/// <summary>
/// 🚨 <b>THE JOIN POINT. Every write that puts THIS process's build identity into the SHARED mesh
/// goes through here, and runs only while this process is admitted.</b> Issue #3478.
///
/// <para><b>What "joining" turned out to be.</b> A portal process becomes addressable long before
/// it can have validated anything — the bake sweep must read NodeTypes through the mesh to have
/// something to validate — so "do not join until validated" cannot mean "do not start". It means:
/// do not publish. The mesh-visible acts that carry this process's identity are few and
/// enumerable:</para>
/// <list type="bullet">
/// <item>the NodeType compile-state stamp, on both write-back paths — the batch bake's
/// storage-level stamp (<c>NodeTypeBatchBake.WriteStamp</c>) and the activation path's
/// (<c>NodeTypeCompilationHelpers</c>). This is the one that produced #3472: it carries
/// <c>CompiledFrameworkVersion</c>, <c>CompiledModulesHash</c>, <c>CompiledDependencies</c> and the
/// assembly coordinates, and every other replica follows it.</item>
/// <item>the module-set ADOPTION record (<c>ModuleSetStore.RecordAdoption</c>) — the durable claim
/// "a replica is serving set N", which a process that will never serve must not make.</item>
/// </list>
///
/// <para>🚨 <b>Assembly BYTES are deliberately NOT gated, and that is not an oversight.</b> The
/// assembly store is keyed <c>(nodeTypePath, version)</c> with the producing framework identity
/// baked into the key, and <c>NodeTypeBakeStatus.ClassifyDetailed</c> cannot look for bytes at all
/// until the RECORD names a collection, a path and a version. Bytes nobody's record points at are
/// inert. The pointer is the poison, so the pointer is what is gated — which keeps the change to
/// three call sites instead of the whole compiler.</para>
///
/// <para><b>Cold, and a factory rather than an observable.</b> <see cref="Publish"/> takes a
/// <c>Func&lt;IObservable&lt;Unit&gt;&gt;</c>: a refused publication is never even CONSTRUCTED, so
/// no storage read is issued and no <c>RequireSubscribeObservable</c> is minted only to be dropped
/// (which would log a spurious never-subscribed warning at GC). A deferred one is constructed at
/// release time, so a compare-and-set write re-reads the row it is racing rather than replaying a
/// version it read before the verdict existed.</para>
///
/// <para><b>Instance state, mesh lifetime.</b> Registered as a mesh-scoped singleton
/// (<c>AddMeshCatalog</c>); every field is an instance field and the queue is an
/// <see cref="ImmutableQueue{T}"/>. Nothing here is static — two test meshes must not share a
/// verdict. The <see cref="verdict"/> lock guards a handful of field assignments and one queue
/// swap and never awaits or subscribes inside itself, exactly like
/// <c>NodeTypeBakeGateState.verdict</c>.</para>
/// </summary>
public sealed class MeshPublicationGate : IDisposable
{
    /// <summary>
    /// Guards <see cref="held"/> and the counters. Writers only, and nothing subscribes or blocks
    /// inside it: the release below takes the queue under the lock and subscribes OUTSIDE it, so a
    /// publication's own pipeline can never run on a thread holding this.
    /// </summary>
    private readonly object verdict = new();

    private readonly IReadOnlyList<IMeshAdmissionAuthority> authorities;
    private readonly ILogger? logger;

    /// <summary>Publications held while <see cref="MeshAdmission.Provisional"/>, in offer order.</summary>
    private ImmutableQueue<HeldPublication> held = ImmutableQueue<HeldPublication>.Empty;

    /// <summary>Subscriptions to released publications, disposed with the gate.</summary>
    private readonly CompositeDisposable releases = new();

    private int heldCount;
    private int passedCount;
    private int releasedCount;
    private int refusedCount;
    private int discardedCount;
    private bool disposed;

    /// <summary>
    /// Constructed by DI. <paramref name="authorities"/> is empty on every host that never armed a
    /// validator — the <see cref="MeshAdmission.Unarmed"/> pass-through — which is what keeps this
    /// inert for dev hosts, tests and every deployment that has not switched the bake gate on.
    /// </summary>
    public MeshPublicationGate(
        IEnumerable<IMeshAdmissionAuthority>? authorities = null,
        ILogger<MeshPublicationGate>? logger = null)
    {
        this.authorities = authorities?.ToImmutableArray() ?? ImmutableArray<IMeshAdmissionAuthority>.Empty;
        this.logger = logger;
    }

    /// <summary>
    /// The MOST RESTRICTIVE verdict across every registered authority, read fresh. An authority
    /// that refuses is stating evidence; one that admits is only stating the absence of it, so the
    /// refusal wins.
    /// </summary>
    public MeshAdmission Admission
    {
        get
        {
            var strongest = MeshAdmission.Unarmed;
            foreach (var authority in authorities)
            {
                var admission = authority.Admission;
                if (Rank(admission) > Rank(strongest))
                    strongest = admission;
            }
            return strongest;
        }
    }

    /// <summary>Why — the reason given by whichever authority produced <see cref="Admission"/>.</summary>
    public string AdmissionReason
    {
        get
        {
            var strongest = MeshAdmission.Unarmed;
            var reason = "no admission authority is armed — publications pass through";
            foreach (var authority in authorities)
            {
                var admission = authority.Admission;
                if (Rank(admission) <= Rank(strongest))
                    continue;
                strongest = admission;
                reason = authority.AdmissionReason;
            }
            return reason;
        }
    }

    /// <summary>Publications currently HELD awaiting a verdict. Diagnostics and tests.</summary>
    public int HeldCount => Volatile.Read(ref heldCount);

    /// <summary>Publications that ran straight through (unarmed or admitted).</summary>
    public int PassedCount => Volatile.Read(ref passedCount);

    /// <summary>Publications that were held and later RELEASED because the verdict admitted.</summary>
    public int ReleasedCount => Volatile.Read(ref releasedCount);

    /// <summary>Publications REFUSED outright — the verdict was already <see cref="MeshAdmission.Refused"/>.</summary>
    public int RefusedCount => Volatile.Read(ref refusedCount);

    /// <summary>Held publications DISCARDED because the verdict turned out to be a refusal.</summary>
    public int DiscardedCount => Volatile.Read(ref discardedCount);

    /// <summary>
    /// Everything this process has decided NOT to put into the mesh — refused outright plus
    /// discarded after being held. The number an operator (and the falsification test) reads as
    /// "this is how much of my identity never reached the shared mesh".
    /// </summary>
    public int WithheldCount => RefusedCount + DiscardedCount;

    /// <summary>
    /// Offer one mesh-visible publication. The returned observable is COLD and the caller must
    /// subscribe it, exactly as before — what changes is what subscribing does:
    ///
    /// <list type="bullet">
    /// <item><see cref="MeshAdmission.Unarmed"/> / <see cref="MeshAdmission.Admitted"/> — the
    /// factory is invoked and its observable is returned verbatim, so the caller's own subscription
    /// drives the write and its errors and completion propagate exactly as they did before this
    /// gate existed.</item>
    /// <item><see cref="MeshAdmission.Provisional"/> — the FACTORY is queued and the caller gets an
    /// immediate completion. The write runs when the verdict admits; it is constructed then, so it
    /// reads the row it is about to compare-and-set rather than one it read before the verdict
    /// existed. A caller must therefore not read "completed" as "the row has changed" — none of the
    /// three call sites does; all three are best-effort stamps whose failure mode is already "the
    /// level-triggered probe re-bakes it".</item>
    /// <item><see cref="MeshAdmission.Refused"/> — the factory is never invoked, nothing is
    /// constructed and nothing is written. Logged at Warning, naming the subject.</item>
    /// </list>
    ///
    /// <para>🚨 The verdict is read at SUBSCRIBE time (<c>Observable.Defer</c>), not
    /// at call time, and it is re-read for every publication. That is what answers "what about a
    /// process that becomes unhealthy AFTER joining?" in the same mechanism and without a second
    /// one: the moment the authority's verdict turns, the next publication is refused.</para>
    /// </summary>
    /// <param name="subject">What is being published — named in every log line. Not an identifier.</param>
    /// <param name="publication">
    /// Builds the cold write. Invoked at most once, and only when the publication will actually be
    /// subscribed.
    /// </param>
    public IObservable<Unit> Publish(string subject, Func<IObservable<Unit>> publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return Observable.Defer(() =>
        {
            var admission = Admission;
            switch (admission)
            {
                case MeshAdmission.Unarmed:
                case MeshAdmission.Admitted:
                    // Anything held before this moment is released on the same edge — so a gate
                    // whose owner forgot to call Reconsider still drains on the next publication.
                    Reconsider();
                    Interlocked.Increment(ref passedCount);
                    return publication();

                case MeshAdmission.Provisional:
                    lock (verdict)
                    {
                        if (disposed)
                            return Observable.Return(Unit.Default);
                        held = held.Enqueue(new HeldPublication(subject, publication));
                        heldCount++;
                    }
                    logger?.LogDebug(
                        "MeshPublicationGate: HOLDING '{Subject}' — this process has not been "
                        + "admitted to the mesh yet ({Reason}). It is published when the "
                        + "validation passes, and discarded if it does not.",
                        subject, AdmissionReason);
                    return Observable.Return(Unit.Default);

                default:
                    Interlocked.Increment(ref refusedCount);
                    logger?.LogWarning(
                        "MeshPublicationGate: REFUSING to publish '{Subject}' — this process FAILED "
                        + "its own validation and must not participate in the mesh ({Reason}). "
                        + "Nothing was written. This is the refusal being REAL: the process stays "
                        + "up for diagnosis but its identity does not enter the shared mesh.",
                        subject, AdmissionReason);
                    // Held work is now known to be worthless — drop it on the same edge.
                    Reconsider();
                    return Observable.Return(Unit.Default);
            }
        });
    }

    /// <summary>
    /// Re-read the verdict and act on any HELD publications: release them when the verdict admits,
    /// discard them when it refuses, leave them alone while it is still provisional.
    ///
    /// <para>The EDGE that <see cref="Publish"/>'s level-triggered read cannot supply by itself: a
    /// process whose last publication was held and whose verdict then turns has nothing left to
    /// drive the queue. The bake gate's owner calls this after every phase transition; the gate
    /// also calls it from <see cref="Publish"/> so a missed call costs latency, never correctness.
    /// Idempotent and cheap when the queue is empty, which is the normal case.</para>
    /// </summary>
    public void Reconsider()
    {
        var admission = Admission;
        if (admission is MeshAdmission.Provisional)
            return;

        ImmutableQueue<HeldPublication> pending;
        lock (verdict)
        {
            if (disposed || held.IsEmpty)
                return;
            pending = held;
            held = ImmutableQueue<HeldPublication>.Empty;
            heldCount = 0;
        }

        var queued = pending.ToImmutableArray();
        if (admission is MeshAdmission.Refused)
        {
            Interlocked.Add(ref discardedCount, queued.Length);
            logger?.LogWarning(
                "MeshPublicationGate: DISCARDING {Count} held publication(s) — this process failed "
                + "its validation and will not join the mesh ({Reason}). Discarded: {Subjects}",
                queued.Length, AdmissionReason,
                string.Join(", ", queued.Select(p => p.Subject)));
            return;
        }

        Interlocked.Add(ref releasedCount, queued.Length);
        logger?.LogInformation(
            "MeshPublicationGate: ADMITTED — releasing {Count} held publication(s) ({Reason})",
            queued.Length, AdmissionReason);

        // Sequential, in offer order, and each publication's own failure is contained: these are
        // best-effort stamps and one that cannot be written must not stop the next.
        var release = queued
            .Select(p => Observable
                .Defer(p.Publication)
                .Catch<Unit, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "MeshPublicationGate: released publication '{Subject}' failed — the next "
                        + "level-triggered probe re-establishes it", p.Subject);
                    return Observable.Return(Unit.Default);
                }))
            .Concat()
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "MeshPublicationGate: the release pipeline faulted — some held publications "
                    + "were not written"));

        lock (verdict)
        {
            if (disposed)
            {
                release.Dispose();
                return;
            }
            releases.Add(release);
        }
    }

    /// <summary>
    /// Mesh teardown. Held publications are dropped — a process that is going away must not write
    /// its identity on the way out — and in-flight releases are cancelled.
    /// </summary>
    public void Dispose()
    {
        lock (verdict)
        {
            if (disposed)
                return;
            disposed = true;
            held = ImmutableQueue<HeldPublication>.Empty;
            heldCount = 0;
        }
        releases.Dispose();
    }

    /// <summary>
    /// Restrictiveness order. <see cref="MeshAdmission.Refused"/> outranks everything: one
    /// authority saying "this image is bad" is evidence, and no number of authorities saying
    /// nothing can outweigh it.
    /// </summary>
    private static int Rank(MeshAdmission admission) => admission switch
    {
        MeshAdmission.Unarmed => 0,
        MeshAdmission.Admitted => 1,
        MeshAdmission.Provisional => 2,
        _ => 3,
    };

    private readonly record struct HeldPublication(string Subject, Func<IObservable<Unit>> Publication);
}
