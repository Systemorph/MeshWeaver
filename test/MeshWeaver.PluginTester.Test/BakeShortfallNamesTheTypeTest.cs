using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The bake-consumption verdict must NAME what declined.
///
/// <para>🚨 The shape this pins was measured on <c>MeshWeaver.Crm</c>: the gate reported "adopted 86
/// of 87 baked assembly(ies) — 1 were DECLINED" and pointed the operator at the per-assembly reason
/// "logged by PrebuiltAssemblySeeder". That reason is logged at <b>Information</b> and the gate mesh
/// runs at <b>Warning</b>, so the log it named contained nothing: <c>main</c> sat red from
/// 2026-08-28 21:54 for ~12 hours with every per-type verdict green and no way to tell which of 87
/// assemblies was the problem. A verdict that cannot be acted on is not a verdict.</para>
///
/// <para>The consumer now witnesses each path the seeder BACKS, so the difference is exact rather
/// than a count. These pin the arithmetic and the naming, without a mesh.</para>
/// </summary>
public class BakeShortfallNamesTheTypeTest
{
    private static ImmutableSortedSet<string> Set(params string[] paths) =>
        ImmutableSortedSet.Create(System.StringComparer.OrdinalIgnoreCase, paths);

    [Fact]
    public void Names_the_declined_type_rather_than_only_counting_it()
    {
        var declared = Set("Crm/Board", "Crm/Client", "Store/Plugin");
        var verdict = BakeSeedConsumer.DescribeShortfall(
            declared,
            requested: declared,
            covered: Set("Crm/Board", "Store/Plugin"),
            directory: "/bake");

        Assert.NotNull(verdict);
        Assert.Contains("DECLINED: Crm/Client", verdict);
        Assert.Contains("adopted 2 of 3", verdict);
        // The types that were fine must not be named as declined.
        Assert.DoesNotContain("DECLINED: Crm/Board", verdict);
    }

    [Fact]
    public void Silent_when_everything_declared_and_requested_was_backed()
    {
        var declared = Set("Crm/Board", "Crm/Client");
        Assert.Null(BakeSeedConsumer.DescribeShortfall(
            declared, requested: declared, covered: declared, directory: "/bake"));
    }

    [Fact]
    public void A_type_the_run_never_installed_is_not_a_shortfall()
    {
        // The bake may carry types this run does not install; only the intersection is owed.
        var verdict = BakeSeedConsumer.DescribeShortfall(
            declared: Set("Crm/Board", "Other/Type"),
            requested: Set("Crm/Board"),
            covered: Set("Crm/Board"),
            directory: "/bake");
        Assert.Null(verdict);
    }

    [Fact]
    public void No_overlap_at_all_still_reports_the_staging_error()
    {
        var verdict = BakeSeedConsumer.DescribeShortfall(
            declared: Set("Crm/Board"),
            requested: Set("Something/Else"),
            covered: Set(),
            directory: "/bake");
        Assert.NotNull(verdict);
        Assert.Contains("NONE of which this run installed", verdict);
    }

    [Fact]
    public void Counting_alone_could_not_distinguish_these_two_runs()
    {
        // Both runs adopted 2 of 3. Only the witnessed set says WHICH one is missing — the whole
        // point of the change, and precisely what a count-based verdict conflated.
        var declared = Set("A/One", "B/Two", "C/Three");
        var missingB = BakeSeedConsumer.DescribeShortfall(
            declared, declared, Set("A/One", "C/Three"), directory: "/bake");
        var missingC = BakeSeedConsumer.DescribeShortfall(
            declared, declared, Set("A/One", "B/Two"), directory: "/bake");

        Assert.Contains("DECLINED: B/Two", missingB);
        Assert.Contains("DECLINED: C/Three", missingC);
        Assert.NotEqual(missingB, missingC);
    }

    [Fact]
    public void A_path_seeded_under_two_packages_is_counted_ONCE()
    {
        // #2697: `Adopted` summed the seeder's per-call counts, so a path covered under two
        // packages counted twice and the verdict could read "adopted 92 of 90" — a number larger
        // than the total it is a fraction of. The count is now derived from the same sets the
        // DECLINED list comes from, so it cannot exceed the denominator however many times the
        // seeder acted.
        var declared = Set("A/One", "B/Two");
        var covered = Set("A/One", "B/Two");

        // Whatever the seeder DID (two packages both seeding both paths = 4 events), the verdict
        // is silent because every expected path is covered...
        Assert.Null(BakeSeedConsumer.DescribeShortfall(
            declared, requested: declared, covered: covered, directory: "/bake"));

        // ...and where there IS a shortfall, adopted + declined == expected, by construction.
        var verdict = BakeSeedConsumer.DescribeShortfall(
            declared, requested: declared, covered: Set("A/One"), directory: "/bake");
        Assert.Contains("adopted 1 of 2", verdict);
        Assert.Contains("1 were DECLINED", verdict);
    }

    [Fact]
    public void AdoptedAmong_counts_the_intersection_not_the_events()
    {
        // The pure seam the property rests on: only paths that are declared AND requested AND
        // covered count, each exactly once.
        Assert.Equal(2, BakeSeedConsumer.AdoptedAmong(
            declared: Set("A/One", "B/Two", "C/Three"),
            requested: Set("A/One", "B/Two"),
            covered: Set("A/One", "B/Two", "C/Three")));
        Assert.Equal(0, BakeSeedConsumer.AdoptedAmong(
            declared: Set("A/One"), requested: Set("B/Two"), covered: Set("A/One", "B/Two")));
    }
}
