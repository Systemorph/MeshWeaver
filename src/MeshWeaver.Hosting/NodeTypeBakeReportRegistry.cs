using System;
using System.Globalization;

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
    DateTimeOffset At);

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

    /// <summary>Records the reading this pass produced, replacing any earlier one.</summary>
    /// <param name="reading">The reading.</param>
    public void Record(BakeReportReading reading) => latest = reading;

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
                   + "measured NOTHING about which of its NodeTypes the share already holds. This "
                   + "is an absence of measurement, NOT a clean bake (#3703).";

        var common =
            $"{reading.Pass} at {reading.At.ToString("O", CultureInfo.InvariantCulture)}: "
            + $"{reading.Summary}. Adoption stamps held by this process: {reading.AdoptionStamps} "
            + "(a count of NodeTypes THIS process stamped from a prebuilt adoption — a different "
            + $"population, in different units, from the {reading.Baked} whose record and the share "
            + "agree; the two are not comparable and never were, #3703).";

        return reading.ClassifiedFromLocalAdoption == 0
            ? common
            : $"the NodeType enumeration snapshot PREDATED this replica's own prebuilt adoptions "
              + $"for {reading.ClassifiedFromLocalAdoption} of {reading.Total} type(s), which were "
              + "therefore classified from the record this process wrote rather than from the "
              + $"snapshot (#3703). {common}";
    }
}
