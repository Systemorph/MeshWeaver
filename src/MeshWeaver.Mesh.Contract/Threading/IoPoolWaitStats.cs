namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// How long work WAITED for an <see cref="IIoPool"/> slot, as a DISTRIBUTION rather than a mean.
///
/// <para>🚨 The distribution is the point. "Is this cap right?" is decided by the TAIL — a pool
/// whose mean wait is a millisecond can still be starving one caller for thirty seconds, and a
/// mean is exactly the statistic that hides it. MeshWeaver#1198 narrowed to this and no further:
/// the Postgres commit stage's writes serialize process-wide behind ONE cap-1 write pool, every
/// partition queued behind every other, and the number 1 has no measurement behind it. Raising
/// the cap without one is the band-aid this repository forbids; this is the reading that would
/// justify a number instead.</para>
///
/// <para><b>Admissions only.</b> A wait that was CANCELLED — a disposal arriving before the slot
/// was granted — contributes nothing. It never became an admission, and folding it in would mix
/// "how long work waited to run" with "how long a teardown took to unwind" in one number.</para>
///
/// <para><b>A lock-free snapshot, not a transaction.</b> The counters are read without a lock, so
/// under concurrent admissions <see cref="Total"/> and <see cref="Max"/> may lag the buckets by a
/// sample or two. <see cref="Samples"/> is DERIVED from the buckets rather than counted separately,
/// so it and the histogram always agree with each other.</para>
/// </summary>
/// <param name="Total">Summed wait across every admission — the numerator of <see cref="Mean"/>.</param>
/// <param name="Max">The longest single wait observed since the pool was created.</param>
/// <param name="UnderMillisecond">Admissions granted in &lt; 1 ms — an uncontended pool.</param>
/// <param name="UnderTenMilliseconds">Admissions granted in [1 ms, 10 ms).</param>
/// <param name="UnderHundredMilliseconds">Admissions granted in [10 ms, 100 ms).</param>
/// <param name="UnderSecond">Admissions granted in [100 ms, 1 s).</param>
/// <param name="UnderTenSeconds">Admissions granted in [1 s, 10 s) — a caller is queueing behind others.</param>
/// <param name="TenSecondsOrMore">Admissions that waited ≥ 10 s — at this end a budget elsewhere has probably already lapsed.</param>
public readonly record struct IoPoolWaitStats(
    TimeSpan Total,
    TimeSpan Max,
    long UnderMillisecond,
    long UnderTenMilliseconds,
    long UnderHundredMilliseconds,
    long UnderSecond,
    long UnderTenSeconds,
    long TenSecondsOrMore)
{
    /// <summary>A pool that has granted nothing — also the value an implementation that does not instrument returns.</summary>
    public static IoPoolWaitStats Empty => default;

    /// <summary>
    /// Total admissions, derived from the buckets so the two can never disagree.
    /// </summary>
    public long Samples =>
        UnderMillisecond + UnderTenMilliseconds + UnderHundredMilliseconds
        + UnderSecond + UnderTenSeconds + TenSecondsOrMore;

    /// <summary>
    /// Mean wait, or <see cref="TimeSpan.Zero"/> when nothing has been admitted. Report it WITH the
    /// buckets, never instead of them — see the type's own remarks on why the tail is the answer.
    /// </summary>
    public TimeSpan Mean => Samples == 0 ? TimeSpan.Zero : new TimeSpan(Total.Ticks / Samples);

    /// <summary>
    /// A one-line reading for a log or a health payload: the count, the mean, the max, and the two
    /// tail buckets that decide whether a cap is starving anyone.
    /// </summary>
    public override string ToString() =>
        $"{Samples} admission(s), mean {Mean.TotalMilliseconds:0.###} ms, max "
        + $"{Max.TotalMilliseconds:0.###} ms, {UnderTenSeconds} over 1 s, {TenSecondsOrMore} over 10 s";
}
