using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting;

/// <summary>
/// The content of the prebuilt-bundle retention pass lease (<see cref="PrebuiltBundleRetentionLease.LeasePath"/>).
///
/// <para>🚨 <b>The decision is ONE number, on purpose.</b> A remote <c>Update</c> reaches the lease
/// node's owner as a three-way merge resolved PER LEAF (<c>MeshNodePatchMerge</c>): a string changed
/// on both sides is REBASED, a leaf the writer's base lacked is APPLIED, and only a non-string scalar
/// changed on both sides is refused whole — and a refusal makes the writer re-run its fold on the live
/// state. A lease spread over a holder string and a date string was therefore TORN by a losing
/// claimant: measured in <c>PrebuiltBundleRetentionLeaseAcrossSilosTest</c>, the owner kept the
/// winner's holder but took the loser's extended hold end and replica name. <see cref="Claim"/> packs
/// the holder and the hold's end into a single <see cref="long"/>, so two claims can only conflict
/// whole, and the loser's fold re-runs and sees the winner.</para>
///
/// <para>🚨 <b>…and a lapsed claim is never the default value.</b> The hub serializer writes with
/// <c>WhenWritingDefault</c>, so a claim of <c>0</c> is OMITTED from the writer's base — and a leaf the
/// base lacks carries no conflict signal, so the owner APPLIES the writer's claim over the winner's.
/// Measured on two silos: both claimants of a lease lapsed at <c>0</c> settled as holders. A lapsed lease
/// therefore carries a negative claim, one per generation (<see cref="PrebuiltBundleRetentionLease.LapsedClaimOf"/>).</para>
/// </summary>
/// <param name="Claim">The hold's end in Unix seconds (high bits) and the holding pass's nonce (low
/// <see cref="PrebuiltBundleRetentionLease.NonceBits"/> bits); <see cref="PrebuiltBundleRetentionLease.LapsedClaim"/>
/// when nobody holds it.</param>
/// <param name="Replica">The process (pod) that took the current — or, when lapsed, the last — claim.
/// Diagnostic only, and written by the confirmed holder after its claim landed, never by a claimant.</param>
/// <param name="TakenAt">When that claim was taken. Diagnostic only, like <paramref name="Replica"/>.</param>
public sealed record PrebuiltBundleRetentionLeaseState(long Claim, string? Replica = null, DateTimeOffset? TakenAt = null)
{
    /// <summary>A lease nobody holds.</summary>
    public static PrebuiltBundleRetentionLeaseState Lapsed { get; } = new(PrebuiltBundleRetentionLease.LapsedClaim);

    /// <summary>
    /// 🚨 <b>The FENCE (#7459).</b> Bumped by every take, in the same write as the <see cref="Claim"/>; a
    /// stamp and a release leave it. A holder carries the generation its take settled and stops (it never
    /// stamps, releases or sweeps on) once a lease of that generation or a NEWER one does not carry its
    /// claim (<see cref="PrebuiltBundleRetentionLease.Lost"/>). The generation is what tells a snapshot from
    /// BEFORE the take (an older generation) from one AFTER the take was lost: the lease node's owner
    /// acknowledged the claim, died before it persisted, reloaded the older lease and granted it to another
    /// replica, whose take carries the same or a later generation. Without it, a loss that came before the
    /// holder's first look at the live lease looked like a stale snapshot, and the holder swept beside the
    /// new one.
    ///
    /// <para>Always WRITTEN, <c>0</c> included. A leaf the writer's base lacks carries no conflict signal
    /// in the owner's merge, so an omitted <c>0</c> would let a stale claimant's generation land over a
    /// newer one. That is the same trap as a lapsed claim of <c>0</c>.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long Generation { get; init; }

    /// <summary>Whether nobody holds the lease: its claim is a lapsed claim (<see cref="PrebuiltBundleRetentionLease.LapsedClaimOf"/>).</summary>
    [JsonIgnore]
    public bool IsLapsed => Claim <= 0;

    /// <summary>The nonce of the pass holding the claim; <c>0</c> when lapsed.</summary>
    [JsonIgnore]
    public int Holder => Claim <= 0 ? 0 : (int)(Claim & PrebuiltBundleRetentionLease.NonceMask & ~PrebuiltBundleRetentionLease.StampedFlag);

    /// <summary>The instant the hold ends; a lease whose hold has ended is free.</summary>
    [JsonIgnore]
    public DateTimeOffset Until => Claim <= 0
        ? DateTimeOffset.MinValue
        : DateTimeOffset.FromUnixTimeSeconds(Claim >> PrebuiltBundleRetentionLease.NonceBits);
}

/// <summary>
/// The outcome of one attempt to take the pass lease.
/// </summary>
/// <param name="Held">Whether THIS attempt holds the lease.</param>
/// <param name="Lease">The lease as the owner settled it — the holder this attempt lost to when
/// <paramref name="Held"/> is false; <c>null</c> when the lease content could not be read, in which
/// case nobody may sweep on it.</param>
public sealed record PrebuiltBundleRetentionLeaseVerdict(bool Held, PrebuiltBundleRetentionLeaseState? Lease);

/// <summary>
/// 🚨 <b>ONE sweep at a time over a SHARED prebuilt-bundle store, across every replica (#7382).</b>
///
/// <para>The store's owner runs the one sweep (Doc/Architecture/SharedArtifactStore) — but an owner
/// with N replicas mounting one volume ran N: every replica swept at boot and on every tick, so the
/// passes enumerated and deleted the same tree at once. Measured on a three-replica owner: one pass
/// collected 787 identities while the other two aborted with "could not read" over directories the
/// first was removing, and nothing stopped two ARMED passes from planning deletions against each
/// other's half-removed state.</para>
///
/// <para><b>The lease is a mesh node, claimed by a FOLD.</b> <see cref="Take"/> writes the lease
/// node through <c>GetMeshNodeStream(path).Update(…)</c>, which the node's owning hub decides one
/// write at a time: two replicas proposing themselves against the same base are serialised there, the
/// second one's <see cref="PrebuiltBundleRetentionLeaseState.Claim"/> is refused as changed since its
/// base, and its fold re-runs on the winner's state and leaves it. The claim is ONE scalar for exactly
/// that reason (see <see cref="PrebuiltBundleRetentionLeaseState"/>). Nothing is locked and nothing
/// waits — a replica that does not hold the lease skips its pass, and the next tick asks again.</para>
///
/// <para><b>Bounded both ways.</b> A pass runs until <see cref="PassDeadline"/> (half the hold) after the
/// instant its claim was dated — taking the lease counts against it, and a pass whose acquisition used it
/// all does not start;
/// past it the pass is unsubscribed, which cancels the pool leaf at its next step boundary — so a
/// pass has the other half of the hold to reach that boundary before another replica may take the
/// lease. The removal itself is fenced entry by entry (<c>PrebuiltBundleStore.DeleteTree</c>, #7443):
/// the token is checked before every file and directory, so a cut pass stops at the entry it is on
/// instead of finishing a whole identity's recursive removal under the next holder. What remains
/// unfenced is ONE entry's own syscall stalling on the share — one file, never a tree. A pass
/// that finishes — or faults — releases the lease at once; a pass cut short by teardown or the
/// deadline leaves it to lapse.</para>
/// </summary>
public static class PrebuiltBundleRetentionLease
{
    /// <summary>The lease's node type.</summary>
    public const string LeaseNodeType = "PrebuiltBundleRetentionLease";

    /// <summary>The namespace the platform's retention leases live in, in the Admin partition every portal has.</summary>
    public const string LeaseNamespace = HomeConfigNodeType.AdminPartition + "/RetentionLease";

    /// <summary>The ONE lease every replica's prebuilt-bundle retention pass must hold to sweep.</summary>
    public const string LeasePath = LeaseNamespace + "/prebuilt-bundles";

    /// <summary>How many low bits of <see cref="PrebuiltBundleRetentionLeaseState.Claim"/> carry the holder's nonce.</summary>
    public const int NonceBits = 29;

    /// <summary>
    /// The claim of a lease nobody holds — deliberately NOT <c>0</c>, which the hub serializer omits, so
    /// a claimant's base always carries the leaf the owner needs to refuse a stale claim.
    /// </summary>
    public const long LapsedClaim = -1L;

    /// <summary>
    /// The claim a lease of <paramref name="generation"/> carries once it is released: <c>-(generation + 1)</c>,
    /// so <see cref="LapsedClaim"/> at generation <c>0</c>, and never <c>0</c>. Pure.
    ///
    /// <para>🚨 <b>One lapsed value per generation (#7459).</b> The owner's merge sees no conflict on a leaf
    /// whose proposed value already IS the live value. With one constant lapsed claim, a take from a STALE
    /// lapsed base (generation <c>g</c>) against a live lease lapsed at <c>g + 1</c> conflicted on neither
    /// leaf: the claim had not changed since the base, and the stale take proposed exactly the live
    /// generation. The take landed at <c>g + 1</c>, the generation a lost take may already have handed to
    /// another holder, whose watch waits past it as a pre-take snapshot. A lapsed claim that names its
    /// generation changes whenever the generation does, so such a take conflicts WHOLE on the claim and its
    /// fold re-runs on the live lease, landing the next generation.</para>
    /// </summary>
    /// <param name="generation">The generation of the lease being released.</param>
    public static long LapsedClaimOf(long generation) => -(Math.Max(generation, 0) + 1);

    /// <summary>The lease <paramref name="lease"/> becomes when released: lapsed, in its own generation. Pure.</summary>
    /// <param name="lease">The lease as read.</param>
    internal static PrebuiltBundleRetentionLeaseState Released(PrebuiltBundleRetentionLeaseState lease)
        => lease with { Claim = LapsedClaimOf(lease.Generation) };

    /// <summary>The mask selecting the nonce bits of a claim.</summary>
    public const long NonceMask = (1L << NonceBits) - 1;

    /// <summary>
    /// The top nonce bit, set by the confirmed holder's STAMP. 🚨 The stamp rewrites the claim scalar
    /// (claim → claim | flag) for the same reason the claim is one scalar: a stamp that changed only the
    /// diagnostic strings was MERGED over another replica's claim when the stamper's mirror was stale
    /// (its base still carried its own lapsed claim), and the stamper read its own computed content back
    /// as held — two sweeps. Rewriting the claim makes a stale stamp conflict whole on the owner, so its
    /// fold re-runs on the live claim and leaves it. Nonces never use this bit (<see cref="NewNonce"/>).
    /// </summary>
    public const long StampedFlag = 1L << (NonceBits - 1);

    /// <summary>
    /// The claim a pass with <paramref name="nonce"/> makes for a hold ending at <paramref name="until"/>
    /// (truncated to the second). Pure.
    /// </summary>
    /// <param name="until">The instant the hold ends.</param>
    /// <param name="nonce">The pass's nonce, in <c>[1, 2^(NonceBits-1))</c> — the top nonce bit is <see cref="StampedFlag"/>.</param>
    public static long ClaimFor(DateTimeOffset until, int nonce)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nonce, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((long)nonce, StampedFlag - 1);
        var seconds = until.ToUnixTimeSeconds();
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        // Past this the shift overflows into the sign bit and the claim reads as lapsed.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(seconds, long.MaxValue >> NonceBits);
        return (seconds << NonceBits) | (long)nonce;
    }

    /// <summary>A fresh pass nonce.</summary>
    public static int NewNonce() => System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, (int)StampedFlag);

    /// <summary>The shortest hold a pass takes, whatever the interval.</summary>
    public static readonly TimeSpan MinimumHold = TimeSpan.FromHours(1);

    /// <summary>
    /// The hold a pass takes for a sweep every <paramref name="interval"/>: most of the interval,
    /// never less than <see cref="MinimumHold"/>. A finished pass releases it early. Pure.
    /// </summary>
    /// <param name="interval">The recurrence interval of the pass.</param>
    public static TimeSpan HoldFor(TimeSpan interval)
    {
        var most = TimeSpan.FromTicks(interval.Ticks / 10 * 9);
        return most > MinimumHold ? most : MinimumHold;
    }

    /// <summary>How long a pass may run under a hold of <paramref name="hold"/>: half of it. Pure.</summary>
    /// <param name="hold">The hold the pass took.</param>
    public static TimeSpan PassDeadline(TimeSpan hold) => TimeSpan.FromTicks(hold.Ticks / 2);

    /// <summary>
    /// Registers the lease's node type, from the service collection so every host that registers the
    /// retention pass gets it with no change to its mesh builder.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddPrebuiltBundleRetentionLease(this IServiceCollection services)
        => services
            .AddSingleton<IStaticNodeProvider, LeaseTypeProvider>()
            .AddSingleton<IEntityCommitGuard, LeaseTransitionCommitGuard>();

    /// <summary>
    /// The refusal for committing <paramref name="after"/> over <paramref name="before"/>, or <c>null</c>
    /// when the transition is whole (#7459). Pure.
    ///
    /// <para>🚨 <b>The claim and its generation move TOGETHER, or not at all.</b> The owner merges a remote
    /// <c>Update</c> leaf by leaf and KEEPS the leaves that did not conflict when another is refused (a
    /// partial refusal NACKs <c>Conflict</c> but commits the rest). A take whose claim was refused could
    /// therefore commit its generation alone: <c>(-2, 1)</c> became <c>(-2, 2)</c>, a lapsed lease one
    /// generation ahead of any take, which a stale claimant could then land on at a generation a lost take
    /// had already handed to another holder, and which a valid holder's fence read as a loss. So at the
    /// commit point a generation advances only with a NEW claim, and a new claim lands only with the NEXT
    /// generation. A stamp (the same claim with <see cref="StampedFlag"/>), a release and the diagnostic
    /// fields keep the generation, and none of them is a new claim.</para>
    /// </summary>
    /// <param name="before">The lease the owner holds.</param>
    /// <param name="after">The lease about to commit.</param>
    internal static string? TransitionRefusal(PrebuiltBundleRetentionLeaseState before, PrebuiltBundleRetentionLeaseState after)
    {
        var from = Math.Max(before.Generation, 0);
        var newClaim = after.Claim > 0 && (after.Claim & ~StampedFlag) != (before.Claim & ~StampedFlag);
        if (newClaim && after.Generation != from + 1)
            return $"the prebuilt-bundle retention lease takes a new claim only with the next generation ({from + 1}), "
                + $"not {after.Generation}: the claim and its generation commit together or not at all";
        if (!newClaim && after.Generation > from)
            return $"the prebuilt-bundle retention lease advances its generation ({from} → {after.Generation}) only "
                + "with a new claim: the claim and its generation commit together or not at all";
        return null;
    }

    /// <summary>
    /// <see cref="TransitionRefusal"/> at the lease node owner's COMMIT point, for every route that commits
    /// into it (#7459). A refused commit commits nothing; the writer's update faults and its next pass asks
    /// again on a fresh read.
    /// </summary>
    private sealed class LeaseTransitionCommitGuard : IEntityCommitGuard
    {
        public string? Refusal(string collection, object? before, object after, string ownerPath,
            AccessContext? writer, System.Text.Json.JsonSerializerOptions options)
        {
            if (!string.Equals(collection, nameof(MeshNode), StringComparison.Ordinal)
                || after.As<MeshNode>(options) is not { NodeType: LeaseNodeType } next
                || before?.As<MeshNode>(options) is not { NodeType: LeaseNodeType } previous
                || previous.ContentAs<PrebuiltBundleRetentionLeaseState>(options) is not { } held
                || next.ContentAs<PrebuiltBundleRetentionLeaseState>(options) is not { } proposed)
                return null;
            return TransitionRefusal(held, proposed);
        }
    }

    /// <summary>
    /// Registers the lease content type on the mesh's shared type registry, so the hub that WRITES
    /// the lease serialises it under the same name the lease node's hub reads. Idempotent.
    /// </summary>
    /// <param name="services">The mesh's service provider.</param>
    public static void RegisterContentType(IServiceProvider services)
        => services.GetRequiredService<ITypeRegistry>()
            .WithType(typeof(PrebuiltBundleRetentionLeaseState), nameof(PrebuiltBundleRetentionLeaseState));

    /// <summary>
    /// Takes the lease for the pass <paramref name="nonce"/> until <paramref name="now"/> +
    /// <paramref name="hold"/>, or learns who holds it. The take is a fold on the lease node's
    /// <see cref="PrebuiltBundleRetentionLeaseState.Claim"/>, decided by its owning hub; a lease whose
    /// content cannot be read is never overwritten (nobody sweeps on it — that is loud, not silent: the
    /// verdict carries a <c>null</c> lease). The confirmed holder then stamps its replica name. As
    /// System. COLD.
    /// </summary>
    /// <param name="hub">The hub to write from — never the dying one; the mesh hub is fine.</param>
    /// <param name="now">The instant of the attempt.</param>
    /// <param name="hold">How long the lease is held unless released.</param>
    /// <param name="nonce">This pass's nonce (<see cref="NewNonce"/>).</param>
    /// <param name="replica">This process's name, recorded for the replicas that skip.</param>
    public static IObservable<PrebuiltBundleRetentionLeaseVerdict> Take(
        IMessageHub hub, DateTimeOffset now, TimeSpan hold, int nonce, string replica)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var claim = ClaimFor(now + hold, nonce);
        return Claim(hub, now, claim)
            .SelectMany(lease => lease is not null && lease.Claim == claim
                // The claim landed: stamp who holds it. Held is decided by what the stamp SETTLED, and
                // only under the generation this take settled (the fence).
                ? Stamp(hub, claim, lease.Generation, replica, now)
                : Observable.Return(new PrebuiltBundleRetentionLeaseVerdict(false, lease)));
    }

    /// <summary>
    /// The claiming fold of <see cref="Take"/>: proposes <paramref name="claim"/>, and the NEXT
    /// <see cref="PrebuiltBundleRetentionLeaseState.Generation"/> in the same write, unless the lease is held
    /// past <paramref name="now"/> or unreadable, and returns the lease as the owner settled it. As System. COLD.
    /// </summary>
    internal static IObservable<PrebuiltBundleRetentionLeaseState?> Claim(IMessageHub hub, DateTimeOffset now, long claim)
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        var options = hub.JsonSerializerOptions;
        var stream = hub.GetWorkspace().GetMeshNodeStream(LeasePath);
        return access.RunAsSystem(() => EnsureLeaseNode(hub)
            .SelectMany(_ => stream
                .Update(current =>
                {
                    var lease = Read(current, options);
                    // Held until a later instant, or unreadable: leave it.
                    if (lease is null || lease.Until > now)
                        return current;
                    // ONLY the claim and its generation: a claimant never writes the diagnostic
                    // fields, so a losing claim has nothing it could land beside the refused scalars.
                    return current with { Content = Taken(lease, claim) };
                })
                .Take(1))
            .Select(settled => Read(settled, options)));
    }

    /// <summary>
    /// The second write of <see cref="Take"/>: stamps <paramref name="replica"/> on the lease only while
    /// <paramref name="claim"/> is still its claim, and marks the claim stamped (<see cref="StampedFlag"/>) in
    /// the same write so a stamp proposed from a STALE mirror conflicts on the claim scalar and is refused. 🚨 The verdict's <see cref="PrebuiltBundleRetentionLeaseVerdict.Held"/>
    /// is read off the lease the stamp SETTLED, never assumed from the claim having landed one write
    /// earlier: a claimant that paused across its own hold's end can find the lease taken by another
    /// replica by the time this fold reads its base — and must then NOT sweep. As System. COLD.
    /// </summary>
    /// <param name="hub">The hub to write from.</param>
    /// <param name="claim">The claim this pass landed.</param>
    /// <param name="generation">The generation the claim landed under: the fence.</param>
    /// <param name="replica">This process's name.</param>
    /// <param name="now">The instant the claim was taken.</param>
    internal static IObservable<PrebuiltBundleRetentionLeaseVerdict> Stamp(
        IMessageHub hub, long claim, long generation, string replica, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var access = hub.ServiceProvider.GetService<AccessService>();
        var options = hub.JsonSerializerOptions;
        return access.RunAsSystem(() => hub.GetWorkspace().GetMeshNodeStream(LeasePath)
            .Update(current => Read(current, options) is { } held && IsHeldBy(held, claim, generation)
                ? current with { Content = held with { Claim = claim | StampedFlag, Replica = replica, TakenAt = now } }
                : current)
            .Take(1)
            .Select(stamped =>
            {
                var lease = Read(stamped, options);
                return new PrebuiltBundleRetentionLeaseVerdict(
                    lease is not null && lease.Claim == (claim | StampedFlag) && lease.Generation == generation, lease);
            }));
    }

    /// <summary>
    /// The lease a take of <paramref name="claim"/> writes over <paramref name="lease"/>: the claim and the
    /// next generation (#7459). Pure.
    /// </summary>
    /// <param name="lease">The lease the take read.</param>
    /// <param name="claim">The claim proposed.</param>
    internal static PrebuiltBundleRetentionLeaseState Taken(PrebuiltBundleRetentionLeaseState lease, long claim)
        => lease with { Claim = claim, Generation = Math.Max(lease.Generation, 0) + 1 };

    /// <summary>
    /// Whether <paramref name="lease"/> still carries <paramref name="claim"/> (stamped or not) under
    /// <paramref name="generation"/>. This is the only state in which its holder may stamp, release or sweep:
    /// a lease of another generation never counts as held, whatever its claim (#7459). Pure.
    /// </summary>
    /// <param name="lease">The lease as read.</param>
    /// <param name="claim">The holder's claim, with or without <see cref="StampedFlag"/>.</param>
    /// <param name="generation">The generation the holder's take settled.</param>
    internal static bool IsHeldBy(PrebuiltBundleRetentionLeaseState? lease, long claim, long generation)
    {
        var unstamped = claim & ~StampedFlag;
        return lease is not null
            && lease.Generation == generation
            && (lease.Claim == unstamped || lease.Claim == (unstamped | StampedFlag));
    }

    /// <summary>
    /// Releases the lease the pass <paramref name="nonce"/> holds — a fold that lapses it only while that
    /// pass's claim is still the claim, so a lease another replica took after a lapse is never touched.
    /// As System. COLD.
    /// </summary>
    /// <param name="hub">The hub to write from.</param>
    /// <param name="nonce">The nonce of the pass that took the lease.</param>
    public static IObservable<Unit> Release(IMessageHub hub, int nonce)
        => ReleaseWhere(hub, lease => lease is { Claim: > 0 } && lease.Holder == nonce);

    /// <summary>
    /// Releases the lease only while it still carries <paramref name="claim"/> under
    /// <paramref name="generation"/> (<see cref="IsHeldBy"/>). A stale holder never lapses a lease of a
    /// newer generation, even one whose claim happens to carry its nonce (#7459). As System. COLD.
    /// </summary>
    /// <param name="hub">The hub to write from.</param>
    /// <param name="claim">The claim the holder's take settled.</param>
    /// <param name="generation">The generation the holder's take settled.</param>
    internal static IObservable<Unit> Release(IMessageHub hub, long claim, long generation)
        => ReleaseWhere(hub, lease => IsHeldBy(lease, claim, generation));

    private static IObservable<Unit> ReleaseWhere(IMessageHub hub, Func<PrebuiltBundleRetentionLeaseState, bool> held)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var access = hub.ServiceProvider.GetService<AccessService>();
        var options = hub.JsonSerializerOptions;
        return access.RunAsSystem(() => hub.GetWorkspace().GetMeshNodeStream(LeasePath)
                .Update(current => Read(current, options) is { } lease && held(lease)
                    ? current with { Content = Released(lease) }
                    : current)
                .Take(1))
            .Select(_ => Unit.Default);
    }

    /// <summary>
    /// Runs <paramref name="pass"/> only while holding the lease. A replica that does not take it runs
    /// nothing and emits nothing (<paramref name="notHeld"/> is told who holds it); the holder's pass is
    /// cut at <see cref="PassDeadline"/>, and cut as soon as the live lease stops naming it.
    ///
    /// <para>🚨 <b>The lease is released ONCE, at the pass's terminal (#7459)</b> — after it COMPLETES
    /// (an empty pass included) or FAULTS, never per result: a pass that emitted and kept working used to
    /// release under itself, so another replica could sweep beside it, and a pass that completed empty
    /// never released at all. The pass's results are held until the release has been attempted and are
    /// then emitted, so a consumer that stops after the first result still finds the lease released.</para>
    ///
    /// <para>🚨 <b>A release that faults never costs the pass its result (#7459).</b> The sweep's outcome
    /// is independent of the lease's cleanup: a release fault goes to <paramref name="releaseFaulted"/>,
    /// the lease is left to lapse at its hold's end, and the results (or the pass's own fault) are
    /// delivered as if the release had succeeded. It used to replace the result with the release's fault,
    /// so a sweep that HAD deleted was recorded as a pass fault that "collected nothing".</para>
    ///
    /// <para>🚨 <b>A lost lease stops the pass (#7459).</b> A holder's verdict rests on its own claim
    /// having been COMMITTED by the lease node's owner, and a commit is acknowledged before it is durable
    /// (<c>DataExtensions.ArmPatchAckWatcher</c> acks on its flush bound). An owner that dies before the
    /// claim persisted is replaced by one that reloads the LAPSED lease and grants it to another replica.
    /// So the holder watches the live lease while its pass runs (<see cref="Lost"/>): once its own claim has
    /// been seen there, any later state that does not carry it (lapsed, unreadable, or another pass's claim)
    /// — and a lease stream that faults or ends — ends the pass with an <see cref="OperationCanceledException"/>, cancelling the pool leaf at its next step
    /// boundary — the same cooperative bound as the deadline — and leaves the lease alone. COLD.</para>
    /// </summary>
    /// <typeparam name="TResult">The pass's result.</typeparam>
    /// <param name="hub">The hub to write the lease from.</param>
    /// <param name="clock">Read when the attempt is SUBSCRIBED — never when it is composed: a pass queued
    /// behind a running one would otherwise take a hold that started in the past and could end while it
    /// still sweeps.</param>
    /// <param name="hold">The hold to take (see <see cref="HoldFor"/>).</param>
    /// <param name="replica">This process's name.</param>
    /// <param name="pass">The pass — subscribed only when the lease is held.</param>
    /// <param name="notHeld">Told the settled lease when this replica does not hold it.</param>
    /// <param name="releaseFaulted">Told the fault of a release that failed after the pass ended; the lease
    /// then lapses at its hold's end and the pass's own outcome is delivered unchanged.</param>
    public static IObservable<TResult> RunLeased<TResult>(
        IMessageHub hub, TimeProvider clock, TimeSpan hold, string replica,
        Func<IObservable<TResult>> pass, Action<PrebuiltBundleRetentionLeaseVerdict> notHeld,
        Action<Exception> releaseFaulted)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(notHeld);
        ArgumentNullException.ThrowIfNull(releaseFaulted);
        return Observable.Defer(() =>
        {
            var now = clock.GetUtcNow();
            var nonce = NewNonce();
            // 🚨 ABSOLUTE: the pass may run until half the hold the CLAIM dated from `now` — taking the
            // lease spends from that budget, it never earns a fresh one.
            var passEnd = now + PassDeadline(hold);
            return Take(hub, now, hold, nonce, replica)
                .SelectMany(verdict =>
                {
                    if (!verdict.Held)
                    {
                        notHeld(verdict);
                        return Observable.Empty<TResult>();
                    }
                    var deadline = passEnd - clock.GetUtcNow();
                    if (deadline <= TimeSpan.Zero)
                        // Acquisition used the whole half-hold: the pass does not start, and the lease is
                        // left to lapse like a cut pass's — say so; this pass is skipped, not cut.
                        return Observable.Throw<TResult>(new OperationCanceledException(
                            $"taking the prebuilt-bundle retention lease outlasted half its hold ({PassDeadline(hold)}), "
                            + "so the pass did not start — its sweep could have overlapped the next holder's; "
                            + "the lease is left to lapse and the next pass re-plans"));
                    return ReleasedAtTerminal(
                        WhileHeld(hub, verdict.Lease!.Claim, verdict.Lease.Generation, Observable.Defer(pass))
                            // Unsubscribing cancels the pool leaf, so a pass past its deadline stops at
                            // its next step boundary — with the other half of the hold to reach it.
                            .Timeout(deadline, Observable.Throw<TResult>(new OperationCanceledException(
                                $"the prebuilt-bundle retention pass outlived half its lease hold ({deadline}) "
                                + "and was stopped, so no other replica's pass can overlap it; the lease is left "
                                + "to lapse and the next pass re-plans what this one did not reach"))),
                        Release(hub, verdict.Lease.Claim, verdict.Lease.Generation),
                        releaseFaulted);
                });
        });
    }

    /// <summary>
    /// The terminal of a held pass (#7459): <paramref name="release"/> is subscribed ONCE, after
    /// <paramref name="pass"/> COMPLETED (an empty pass included) or FAULTED — never per result, and never
    /// after an <see cref="OperationCanceledException"/> (teardown, the deadline, a lost lease: the pass may
    /// still be winding down its leaf, so the lease is left to lapse). The pass's results are held until
    /// the release was attempted, then emitted; a release fault goes to <paramref name="releaseFaulted"/>
    /// and never replaces the pass's results or its own fault. Pure composition. COLD.
    /// </summary>
    /// <typeparam name="TResult">The pass's result.</typeparam>
    /// <param name="pass">The pass, already bounded.</param>
    /// <param name="release">Releases the lease.</param>
    /// <param name="releaseFaulted">Told a release's fault.</param>
    internal static IObservable<TResult> ReleasedAtTerminal<TResult>(
        IObservable<TResult> pass, IObservable<Unit> release, Action<Exception> releaseFaulted)
    {
        var released = release
            .Catch<Unit, Exception>(releaseFault =>
            {
                releaseFaulted(releaseFault);
                return Observable.Empty<Unit>();
            })
            .LastOrDefaultAsync();
        return pass
            .ToList()
            .Catch<IList<TResult>, Exception>(ex => ex is OperationCanceledException
                ? Observable.Throw<IList<TResult>>(ex)
                : released.SelectMany(_ => Observable.Throw<IList<TResult>>(ex)))
            .SelectMany(results => released.SelectMany(_ => results));
    }

    /// <summary>
    /// <paramref name="pass"/>, ended with an <see cref="OperationCanceledException"/> as soon as the live
    /// lease no longer names <paramref name="stampedClaim"/> under <paramref name="generation"/>
    /// (<see cref="Lost"/>): the lease node's owner reloaded a lease whose claim had not persisted, another
    /// replica holds it, or the lease can no longer be followed. Unsubscribing the pass cancels its pool leaf
    /// at the next step boundary. As System. COLD.
    /// </summary>
    private static IObservable<TResult> WhileHeld<TResult>(IMessageHub hub, long stampedClaim, long generation, IObservable<TResult> pass)
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        var options = hub.JsonSerializerOptions;
        var lost = Lost(
                access.RunAsSystem(() => hub.GetWorkspace().GetMeshNodeStream(LeasePath)
                    .Select(node => Read(node, options))),
                stampedClaim,
                generation)
            .SelectMany(cancelled => Observable.Throw<TResult>(cancelled));
        return pass.Publish(shared => shared.Merge(lost.TakeUntil(shared.LastOrDefaultAsync())));
    }

    /// <summary>
    /// Emits ONE <see cref="OperationCanceledException"/> when the holder of <paramref name="stampedClaim"/>
    /// must stop, read off the <paramref name="live"/> lease (#7459). Pure composition. COLD.
    ///
    /// <list type="bullet">
    ///   <item><description><b>Lost before it was seen: the fence.</b> A lease of
    ///     <paramref name="generation"/> or a NEWER one that does not carry this claim means another take
    ///     came after this one: the owner acknowledged this claim, lost it before it persisted, and granted
    ///     the lease again. That is a loss even when it is the first state the watch sees. A lease of an OLDER
    ///     generation is a snapshot from before the take (or the reload of one). No other replica can sweep on
    ///     it without taking a newer generation, so it is waited past.</description></item>
    ///   <item><description><b>Lost after it was seen.</b> Once the live lease has carried this claim, any
    ///     later state that does not (lapsed, unreadable, another pass's claim, another generation) is a
    ///     loss.</description></item>
    ///   <item><description><b>The lease can no longer be followed.</b> A fault of the live lease, or its
    ///     completion, leaves the holder unable to see a loss, so it is one: a cancellation (the fault kept
    ///     as its inner exception), never an ordinary pass fault — a pass that FAULTS releases at once, under
    ///     a leaf that may still be winding down, while a cancelled one leaves the lease to lapse.</description></item>
    /// </list>
    /// </summary>
    /// <param name="live">The lease as the live stream carries it; <c>null</c> when its content cannot be read.</param>
    /// <param name="stampedClaim">The claim the holder's stamp settled.</param>
    /// <param name="generation">The generation the holder's take settled: the fence.</param>
    internal static IObservable<OperationCanceledException> Lost(
        IObservable<PrebuiltBundleRetentionLeaseState?> live, long stampedClaim, long generation)
    {
        bool Ours(PrebuiltBundleRetentionLeaseState? lease) => IsHeldBy(lease, stampedClaim, generation);
        // Before the claim is seen, only a state of this generation or a newer one says the take was superseded.
        bool Superseded(PrebuiltBundleRetentionLeaseState? lease) => lease is not null && lease.Generation >= generation && !Ours(lease);
        return live
            .SkipWhile(lease => !Ours(lease) && !Superseded(lease))
            .Where(lease => !Ours(lease))
            .Select(lease => new OperationCanceledException(
                "the prebuilt-bundle retention lease this pass held no longer names it (now: "
                + (lease is null ? "unreadable" : lease.Claim <= 0 ? "lapsed" : $"pass {lease.Holder}")
                + (lease is null ? "" : $" at generation {lease.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
                + $"; this pass took generation {generation.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                + ") — its owner lost the claim or another replica took it, so this pass stops at its next "
                + "step boundary rather than sweep beside another; the next pass re-plans what it did not reach"))
            .Catch<OperationCanceledException, Exception>(fault => Observable.Return(new OperationCanceledException(
                "the prebuilt-bundle retention lease this pass held can no longer be followed, so a loss could not be "
                + "seen — the pass stops at its next step boundary and leaves the lease to lapse", fault)))
            .Concat(Observable.Defer(() => Observable.Return(new OperationCanceledException(
                "the prebuilt-bundle retention lease this pass held stopped being followed (its stream ended), so a "
                + "loss could not be seen — the pass stops at its next step boundary and leaves the lease to lapse"))))
            .Take(1);
    }

    /// <summary>
    /// Creates the lease node when it does not exist yet — a <c>scope:children</c> listing for the
    /// existence, never a point read of a node that may be absent; a stale negative ends in a create
    /// the store refuses as already existing, which is the same answer.
    /// </summary>
    private static IObservable<Unit> EnsureLeaseNode(IMessageHub hub)
    {
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        return mesh.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{LeaseNamespace} scope:children select:path"))
            .Take(1)
            .Select(change => change.Items.Any(n => string.Equals(n.Path, LeasePath, StringComparison.Ordinal)))
            .SelectMany(exists => exists
                ? Observable.Return(Unit.Default)
                : mesh.CreateNode(MeshNode.FromPath(LeasePath) with
                    {
                        NodeType = LeaseNodeType,
                        Name = "Prebuilt-bundle retention lease",
                        State = MeshNodeState.Active,
                        Content = PrebuiltBundleRetentionLeaseState.Lapsed,
                    })
                    .Take(1)
                    .Select(_ => Unit.Default)
                    .Catch<Unit, Exception>(ex => ex.IsNodeAlreadyExists()
                        ? Observable.Return(Unit.Default)
                        : Observable.Throw<Unit>(ex)));
    }

    /// <summary>The lease on <paramref name="node"/>: lapsed when it carries no content, <c>null</c> when its content cannot be read.</summary>
    private static PrebuiltBundleRetentionLeaseState? Read(MeshNode? node, System.Text.Json.JsonSerializerOptions options)
        => node?.Content is null
            ? PrebuiltBundleRetentionLeaseState.Lapsed
            : node.ContentAs<PrebuiltBundleRetentionLeaseState>(options);

    /// <summary>Contributes the lease's node type.</summary>
    private sealed class LeaseTypeProvider : IStaticNodeProvider
    {
        public IEnumerable<MeshNode> GetStaticNodes()
        {
            yield return new MeshNode(LeaseNodeType)
            {
                Name = "Prebuilt-bundle retention lease",
                ExcludeFromContext = ImmutableHashSet.Create("search", "create", "content"),
                HubConfiguration = config => config
                    .WithType<PrebuiltBundleRetentionLeaseState>(nameof(PrebuiltBundleRetentionLeaseState))
                    .AddMeshDataSource(source => source.WithContentType<PrebuiltBundleRetentionLeaseState>()),
            };
        }
    }
}
