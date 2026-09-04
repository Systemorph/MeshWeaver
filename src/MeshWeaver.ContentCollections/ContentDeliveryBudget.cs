using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// 🚨 <b>Issue #3101 — what a content delivery WEIGHS, and against WHICH limit.</b> The one place
/// that answers both, so a partitioner and a refusal report can never disagree about the numbers.
///
/// <para><b>Why this exists as its own type.</b> The producer
/// (<see cref="SyncContentFilesBuilder"/>) already measured every file in order to split the write
/// across deliveries — and then threw the measurement away. When the transport refused what it
/// handed over, all that survived was <c>Success == false</c>: the importer folded it to a bare node
/// path and the activity could only GUESS at the cause ("most often a delivery over the transport's
/// size budget"). The facts that would have named it — this file, this many packaged bytes, that
/// limit — were in hand at the moment of the post and were never written down. Naming them costs
/// nothing: the cost function never touches the bytes.</para>
///
/// <para>🚨 <b>This type measures; it never refuses.</b> The budget is the ORLEANS memory-stream
/// block size, which binds only where that transport is in the path — a monolith carries an
/// over-budget file perfectly well. Turning the measurement into a producer-side rejection would
/// stop content that works today from syncing, which is the opposite of the defect: the bug is a
/// refusal nobody can see, not a delivery nobody refused. So the answer is a DESCRIPTION, attached
/// to a failure that already happened.</para>
/// </summary>
public static class ContentDeliveryBudget
{
    /// <summary>
    /// How many packaged (base64) bytes of file content one delivery may accumulate — Orleans'
    /// memory-stream block size (<see cref="DeliveryPayloadBounds.MemoryStreamBlockBytes"/>,
    /// 1,048,576), the tighter of the two transport ceilings on one message and the one a failure
    /// report about a delivery must itself survive. Hard-coded in Orleans' <c>MemoryAdapterFactory</c>
    /// with no configuration surface, so it is not a number to raise.
    /// </summary>
    public const int BudgetBytes = DeliveryPayloadBounds.MemoryStreamBlockBytes;

    /// <summary>
    /// What one file costs the packaged payload: its bytes as base64, plus its path. Exact enough to
    /// partition against and to report, and it never touches the bytes.
    /// </summary>
    /// <param name="file">The inline file to measure.</param>
    public static long PackagedCost(InlineContentFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return PackagedCost(file.Path, file.Content.Length);
    }

    /// <summary>
    /// The packaged cost of a file given its path and raw byte length —
    /// <c>4 × ⌈length / 3⌉ + path.Length</c>, i.e. the base64 expansion plus the path that rides
    /// beside it. <c>System.Text.Json</c> renders a <c>byte[]</c> as base64, so this is what the
    /// message actually weighs.
    /// </summary>
    /// <param name="path">The file's path within the delivery.</param>
    /// <param name="contentLength">The file's raw byte count.</param>
    public static long PackagedCost(string? path, int contentLength)
        => 4L * ((contentLength + 2) / 3) + (path?.Length ?? 0);

    /// <summary>
    /// Names the files that exceed <see cref="BudgetBytes"/> <b>on their own</b> — the ones a split
    /// cannot help, because a file is the atom the receiving handler writes and is never divided. So
    /// the delivery carrying one is over the budget by construction, however the rest of the set is
    /// partitioned.
    ///
    /// <para>Returns <c>null</c> when every file fits, so a caller can attach the sentence only when
    /// it is true — a refusal that had nothing to do with size must not be reported as if it did.
    /// That negative is the case that could falsify this: an unrelated failure (a missing collection,
    /// an unsafe path) still reports its own reason and nothing more.</para>
    ///
    /// <para>Measured 2026-09-03 against <c>MeshWeaver.Education@61cbbac</c> and again 2026-09-04
    /// against <c>@f7ae723</c>, unchanged: 25 files across 7 Spaces are individually over this
    /// budget, the largest packaging to 12.6 MB — twelve times the ceiling. The axis is "has a
    /// video", not "is large": the third-SMALLEST Space in that repo carries the second-largest
    /// single file.</para>
    ///
    /// <para>🚨 <b>#3233 — pass this the files that ACTUALLY TRAVELLED INLINE.</b> A file over the
    /// budget now goes out of band (its bytes into the destination collection's staging folder, a
    /// handle on the delivery), so it is no longer an over-budget payload and describing it as one
    /// would be a sentence about a delivery nobody built. The caller therefore measures the inline
    /// remainder, which is empty of over-budget files whenever staging ran — and is exactly the
    /// original set whenever it could not.</para>
    /// </summary>
    /// <param name="files">The files the delivery (or set of deliveries) carries.</param>
    /// <returns>A one-line description of the over-budget files, or <c>null</c> when there are none.</returns>
    public static string? DescribeOverBudget(IReadOnlyCollection<InlineContentFile>? files)
    {
        if (files is null || files.Count == 0)
            return null;

        var overBudget = files
            .Select(f => (f.Path, Cost: PackagedCost(f)))
            .Where(x => x.Cost > BudgetBytes)
            .OrderByDescending(x => x.Cost)
            .ToArray();
        if (overBudget.Length == 0)
            return null;

        var largest = overBudget[0];
        var total = files.Sum(PackagedCost);
        return $"{overBudget.Length} of {files.Count} file(s) exceed the {BudgetBytes:N0}-byte "
            + "per-delivery content budget ON THEIR OWN, and a file is never split — so the delivery "
            + $"carrying one is over the budget however the set is partitioned. Largest: "
            + $"'{largest.Path}' at {largest.Cost:N0} packaged bytes "
            + $"({(double)largest.Cost / BudgetBytes:0.0}× the budget). "
            + $"{total:N0} packaged bytes of content in total. Assets this size travel behind a "
            + "content-store handle rather than as an inline payload — see "
            + "Doc/Architecture/OutOfBandContentTransfer.";
    }
}
