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
            adopted: 2,
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
            declared, requested: declared, covered: declared, adopted: 2, directory: "/bake"));
    }

    [Fact]
    public void A_type_the_run_never_installed_is_not_a_shortfall()
    {
        // The bake may carry types this run does not install; only the intersection is owed.
        var verdict = BakeSeedConsumer.DescribeShortfall(
            declared: Set("Crm/Board", "Other/Type"),
            requested: Set("Crm/Board"),
            covered: Set("Crm/Board"),
            adopted: 1,
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
            adopted: 0,
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
            declared, declared, Set("A/One", "C/Three"), adopted: 2, directory: "/bake");
        var missingC = BakeSeedConsumer.DescribeShortfall(
            declared, declared, Set("A/One", "B/Two"), adopted: 2, directory: "/bake");

        Assert.Contains("DECLINED: B/Two", missingB);
        Assert.Contains("DECLINED: C/Three", missingC);
        Assert.NotEqual(missingB, missingC);
    }
}
